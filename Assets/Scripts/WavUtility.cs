using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Converts Unity's float audio samples into 16-bit PCM WAV data.
///
/// We need this twice over: now, to save utterances to disk so we can listen
/// back and check the detection boundaries; and later, to package audio for
/// sending to the Whisper server. Writing it once, properly, serves both.
///
/// WHY 16-BIT PCM:
/// Unity gives audio as floats from -1 to 1. WAV files conventionally store
/// 16-bit signed integers (-32768 to 32767). Whisper and virtually every
/// audio tool expect that format, so we convert on the way out.
/// </summary>
public static class WavUtility
{
    private const int HeaderSize = 44;

    /// <summary>
    /// Encodes mono float samples into a complete WAV file in memory.
    /// </summary>
    public static byte[] EncodeToWav(float[] samples, int sampleCount, AudioFormat format)
    {
        if (samples == null || sampleCount <= 0)
        {
            Debug.LogError("[WavUtility] No samples to encode.");
            return null;
        }

        int channels = Mathf.Max(1, format.channels);
        int sampleRate = format.sampleRate;
        const int bitsPerSample = 16;

        int dataSize = sampleCount * channels * (bitsPerSample / 8);

        using (MemoryStream stream = new MemoryStream(HeaderSize + dataSize))
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII))
        {
            // --- RIFF chunk ---
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);          // total file size minus the first 8 bytes
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            // --- fmt subchunk: describes the audio format ---
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);                     // size of this subchunk
            writer.Write((short)1);               // 1 = uncompressed PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8)); // byte rate
            writer.Write((short)(channels * (bitsPerSample / 8)));     // block align
            writer.Write((short)bitsPerSample);

            // --- data subchunk: the samples themselves ---
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            for (int i = 0; i < sampleCount; i++)
            {
                // Clamp first: values outside -1..1 would wrap around and
                // turn a loud sound into harsh distortion.
                float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                writer.Write((short)(clamped * short.MaxValue));
            }

            writer.Flush();
            return stream.ToArray();
        }
    }

    /// <summary>
    /// Encodes and writes a WAV file to disk. Returns the full path written.
    /// </summary>
    public static string SaveToFile(float[] samples, int sampleCount, AudioFormat format, string directory, string fileName)
    {
        byte[] wav = EncodeToWav(samples, sampleCount, format);
        if (wav == null) return null;

        try
        {
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, wav);

            return path;
        }
        catch (Exception e)
        {
            Debug.LogError($"[WavUtility] Failed to write WAV file: {e.Message}");
            return null;
        }
    }
}
