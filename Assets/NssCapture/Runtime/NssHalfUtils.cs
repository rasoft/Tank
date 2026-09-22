using System;
using UnityEngine;

// IEEE 754 half-float decoding and abnormal-value scanning for readback buffers.
public static class NssHalfUtils
{
    public static float HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 1;
        int exp = (h >> 10) & 0x1F;
        int man = h & 0x3FF;

        float v;
        if (exp == 0)
            v = man * 5.960464477539063e-08f; // subnormal: man * 2^-24
        else if (exp == 31)
            v = man == 0 ? float.PositiveInfinity : float.NaN;
        else
            v = (1024 + man) * Mathf.Pow(2, exp - 25);

        return sign == 1 ? -v : v;
    }

    // Scans a tightly packed little-endian half buffer for NaN/Inf (exp == 0x1F).
    public static void CountHalfAbnormal(byte[] data, int halfCount, out int nanCount, out int infCount)
    {
        nanCount = 0;
        infCount = 0;
        for (int i = 0; i < halfCount; i++)
        {
            ushort h = (ushort)(data[i * 2] | (data[i * 2 + 1] << 8));
            if ((h & 0x7C00) != 0x7C00)
                continue;
            if ((h & 0x03FF) == 0) infCount++;
            else nanCount++;
        }
    }

    // Scans a packed float32 buffer for NaN/Inf.
    public static void CountFloatAbnormal(byte[] data, int floatCount, out int nanCount, out int infCount)
    {
        nanCount = 0;
        infCount = 0;
        for (int i = 0; i < floatCount; i++)
        {
            uint u = (uint)(data[i * 4] | (data[i * 4 + 1] << 8) | (data[i * 4 + 2] << 16) | (data[i * 4 + 3] << 24));
            if ((u & 0x7F800000) != 0x7F800000)
                continue;
            if ((u & 0x007FFFFF) == 0) infCount++;
            else nanCount++;
        }
    }
}
