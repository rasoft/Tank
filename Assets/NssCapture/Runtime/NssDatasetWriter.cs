using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

public class NssFramePayload
{
    public int SequenceId;
    public int FrameInSequence;
    public long FrameId;
    public float SimTimestamp;
    public bool CameraCut;
    public string MetadataJson;
    public byte[] LrColorExr;
    public byte[] GtColorExr;
    public byte[] MotionExr;
    public byte[] DepthExr;
    public byte[] VizMotionPng;
    public byte[] VizDepthPng;
    public byte[] VizLrPng;
    public byte[] VizDiffPng;
}

// Background file writer. Frames are enqueued tagged with (sequence, frame) so
// asynchronous readback completion order can never break the Frame ID binding.
public class NssDatasetWriter
{
    class SeqStats
    {
        public long FirstFrame = long.MaxValue;
        public long LastFrame;
        public int Count;
        public float SimStart;
        public float SimEnd;
        public readonly List<long> Cuts = new List<long>();
        public int Nan;
        public int Inf;
    }

    readonly BlockingCollection<NssFramePayload> _queue;
    readonly Task _task;
    readonly string _root;
    readonly object _lock = new object();
    readonly Dictionary<int, SeqStats> _seqStats = new Dictionary<int, SeqStats>();
    readonly List<string> _issues = new List<string>();

    public long FramesWritten;

    public NssDatasetWriter(string rootAbs)
    {
        _root = rootAbs;
        Directory.CreateDirectory(rootAbs);
        _queue = new BlockingCollection<NssFramePayload>(4096);
        _task = Task.Run(WriteLoop);
    }

    public void Enqueue(NssFramePayload p)
    {
        if (_queue.IsAddingCompleted) return;
        try { _queue.Add(p); }
        catch (Exception e) { lock (_lock) _issues.Add("enqueue failed: " + e.Message); }
    }

    void WriteLoop()
    {
        try
        {
            foreach (var p in _queue.GetConsumingEnumerable())
                WriteOne(p);
        }
        catch (Exception e)
        {
            lock (_lock) _issues.Add("writer loop aborted: " + e.Message);
        }
    }

    void WriteOne(NssFramePayload p)
    {
        try
        {
            string frameDir = Path.Combine(_root, $"sequence_{p.SequenceId:D4}", $"frame_{p.FrameInSequence:D6}");
            Directory.CreateDirectory(frameDir);
            if (p.LrColorExr != null) File.WriteAllBytes(Path.Combine(frameDir, "lr_color.exr"), p.LrColorExr);
            if (p.GtColorExr != null) File.WriteAllBytes(Path.Combine(frameDir, "gt_color.exr"), p.GtColorExr);
            if (p.MotionExr != null) File.WriteAllBytes(Path.Combine(frameDir, "motion.exr"), p.MotionExr);
            if (p.DepthExr != null) File.WriteAllBytes(Path.Combine(frameDir, "depth_linear.exr"), p.DepthExr);
            if (p.MetadataJson != null) File.WriteAllText(Path.Combine(frameDir, "metadata.json"), p.MetadataJson);

            if (p.VizMotionPng != null || p.VizDepthPng != null || p.VizLrPng != null || p.VizDiffPng != null)
            {
                string vizDir = Path.Combine(_root, $"sequence_{p.SequenceId:D4}", "viz");
                Directory.CreateDirectory(vizDir);
                string tag = $"frame_{p.FrameInSequence:D6}";
                if (p.VizMotionPng != null) File.WriteAllBytes(Path.Combine(vizDir, tag + "_motion.png"), p.VizMotionPng);
                if (p.VizDepthPng != null) File.WriteAllBytes(Path.Combine(vizDir, tag + "_depth.png"), p.VizDepthPng);
                if (p.VizLrPng != null) File.WriteAllBytes(Path.Combine(vizDir, tag + "_lr.png"), p.VizLrPng);
                if (p.VizDiffPng != null) File.WriteAllBytes(Path.Combine(vizDir, tag + "_gt_lr_diff.png"), p.VizDiffPng);
            }

            lock (_lock)
            {
                if (!_seqStats.TryGetValue(p.SequenceId, out var s))
                {
                    s = new SeqStats();
                    _seqStats[p.SequenceId] = s;
                }
                if (p.FrameId < s.FirstFrame) s.FirstFrame = p.FrameId;
                if (p.FrameId > s.LastFrame) s.LastFrame = p.FrameId;
                if (s.Count == 0) s.SimStart = p.SimTimestamp;
                s.SimEnd = p.SimTimestamp;
                s.Count++;
                if (p.CameraCut) s.Cuts.Add(p.FrameId);
                s.Nan += p.Qa()?.nan_count ?? 0;
                s.Inf += p.Qa()?.inf_count ?? 0;
            }
            FramesWritten++;
        }
        catch (Exception e)
        {
            lock (_lock) _issues.Add($"frame {p.FrameId}: {e.Message}");
        }
    }

    // Completes the queue, waits for the writer thread, then writes sequence.json files.
    public void EndAndWait(int timeoutMs)
    {
        _queue.CompleteAdding();
        try { _task.Wait(timeoutMs); }
        catch (Exception e) { lock (_lock) _issues.Add("writer wait failed: " + e.Message); }
        FinalizeSequences();
    }

    void FinalizeSequences()
    {
        lock (_lock)
        {
            foreach (var kv in _seqStats)
            {
                try
                {
                    var s = kv.Value;
                    var seq = new NssSequenceJson
                    {
                        sequence_id = kv.Key,
                        first_frame_id = s.FirstFrame,
                        last_frame_id = s.LastFrame,
                        frame_count = s.Count,
                        sim_start = s.SimStart,
                        sim_end = s.SimEnd,
                        camera_cut_frames = s.Cuts.ToArray(),
                        nan_count = s.Nan,
                        inf_count = s.Inf
                    };
                    if (s.LastFrame - s.FirstFrame + 1 != s.Count)
                        seq.notes = "WARNING: frame gap detected in this sequence";
                    File.WriteAllText(Path.Combine(_root, $"sequence_{kv.Key:D4}", "sequence.json"),
                        JsonUtility.ToJson(seq, true));
                }
                catch (Exception e)
                {
                    _issues.Add($"sequence {kv.Key} summary failed: {e.Message}");
                }
            }
        }
    }

    public List<string> GetIssues()
    {
        lock (_lock) return new List<string>(_issues);
    }

    public List<NssSequenceJson> GetSequenceSummaries()
    {
        lock (_lock)
        {
            var list = new List<NssSequenceJson>();
            foreach (var kv in _seqStats)
            {
                var s = kv.Value;
                list.Add(new NssSequenceJson
                {
                    sequence_id = kv.Key,
                    first_frame_id = s.FirstFrame,
                    last_frame_id = s.LastFrame,
                    frame_count = s.Count,
                    sim_start = s.SimStart,
                    sim_end = s.SimEnd,
                    camera_cut_frames = s.Cuts.ToArray(),
                    nan_count = s.Nan,
                    inf_count = s.Inf
                });
            }
            return list;
        }
    }
}

public static class NssFramePayloadQa
{
    // Lightweight parse of nan/inf counts back out of the serialized metadata JSON
    // (kept string-only in the payload to avoid duplicating object graphs).
    public static NssFrameQa Qa(this NssFramePayload p)
    {
        if (p.MetadataJson == null) return null;
        int i = p.MetadataJson.IndexOf("\"nan_count\"");
        int j = p.MetadataJson.IndexOf("\"inf_count\"");
        if (i < 0 || j < 0) return null;
        var qa = new NssFrameQa();
        qa.nan_count = ParseIntAt(p.MetadataJson, i);
        qa.inf_count = ParseIntAt(p.MetadataJson, j);
        return qa;
    }

    static int ParseIntAt(string json, int from)
    {
        int colon = json.IndexOf(':', from);
        if (colon < 0) return 0;
        int start = colon + 1;
        while (start < json.Length && (char.IsWhiteSpace(json[start]))) start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        return int.TryParse(json.Substring(start, end - start), out int v) ? v : 0;
    }
}
