using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace HeadphoneSwitcher
{
    internal static class SoundCues
    {
        public static bool TryPlay(int cue, string endpointId, Action<string> log)
        {
            try
            {
                using (var gate = new OperationGate(AppPaths.ConfigPath + ".sound", 0))
                {
                    if (!gate.Acquired) { log("Sound cue skipped: another cue is playing."); return false; }
                    // Retry opening the endpoint briefly while USB/Bluetooth finishes becoming ready.
                    Exception last = null;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try { Play(cue, endpointId); return true; }
                        catch (Exception ex) { last = ex; if (attempt < 4) Thread.Sleep(100); }
                    }
                    throw last;
                }
            }
            catch (Exception ex) { log("Sound cue could not play; device switch result is unchanged. " + ex.Message); return false; }
        }
        internal static byte[] Samples(int cue, out WaveFormat format)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(cue == 0 ? "AudioSwitcher.Error.wav" : "AudioSwitcher.Confirm.wav"))
            {
                if (stream == null) throw new FileNotFoundException("The notification sound is missing.");
                using (var reader = new BinaryReader(stream))
                {
                    if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Invalid sound file.");
                    reader.ReadUInt32();
                    if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Invalid sound file.");
                    format = new WaveFormat();
                    byte[] samples = null;
                    while (stream.Position + 8 <= stream.Length)
                    {
                        string kind = new string(reader.ReadChars(4));
                        int size = reader.ReadInt32();
                        long next = stream.Position + size + (size & 1);
                        if (size < 0 || next > stream.Length) throw new InvalidDataException("Invalid sound chunk.");
                        if (kind == "fmt ")
                        {
                            format.Tag = reader.ReadUInt16(); format.Channels = reader.ReadUInt16();
                            format.Rate = reader.ReadUInt32(); format.BytesPerSecond = reader.ReadUInt32();
                            format.BlockAlign = reader.ReadUInt16(); format.Bits = reader.ReadUInt16();
                        }
                        else if (kind == "data") samples = reader.ReadBytes(size);
                        stream.Position = next;
                    }
                    if (samples == null || format.Tag != 1 || format.Bits != 16 || format.BlockAlign == 0)
                        throw new InvalidDataException("Sounds must use 16-bit PCM WAV.");
                    samples = PrepareClip(samples, format);
                    if (cue != 2) return samples;
                    return RisingPair(samples, format);
                }
            }
        }
        internal static byte[] RisingPair(byte[] samples, WaveFormat format)
        {
            // Start the second strike during the first note's decay. A whole-tone lift is subtle
            // enough to keep the same character while making profile 2 an ascending confirmation.
            int frames = samples.Length / format.BlockAlign;
            int secondStart = (int)(format.Rate * 0.18);
            double pitchRatio = Math.Pow(2.0, 2.0 / 12.0);
            int secondFrames = (int)Math.Ceiling(frames / pitchRatio);
            int totalFrames = Math.Max(frames, secondStart + secondFrames);
            var pair = new byte[totalFrames * format.BlockAlign];
            for (int frame = 0; frame < totalFrames; frame++)
            {
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    double value = frame < frames ? BitConverter.ToInt16(samples, frame * format.BlockAlign + channel * 2) : 0;
                    if (frame >= secondStart && frame < secondStart + secondFrames)
                    {
                        double source = (frame - secondStart) * pitchRatio;
                        int left = (int)source;
                        int right = Math.Min(left + 1, frames - 1);
                        double fraction = source - left;
                        double a = BitConverter.ToInt16(samples, left * format.BlockAlign + channel * 2);
                        double b = BitConverter.ToInt16(samples, right * format.BlockAlign + channel * 2);
                        value += a + (b - a) * fraction;
                    }
                    // Keep the combined tail and second strike comfortably below full scale.
                    short mixed = (short)Math.Round(Math.Max(-6553, Math.Min(6553, value)));
                    int offset = frame * format.BlockAlign + channel * 2;
                    pair[offset] = (byte)(mixed & 255); pair[offset + 1] = (byte)((mixed >> 8) & 255);
                }
            }
            return pair;
        }
        internal static byte[] PrepareClip(byte[] samples, WaveFormat format)
        {
            if (format.Channels < 1 || format.BlockAlign != format.Channels * 2 || samples.Length % format.BlockAlign != 0 || format.Rate == 0)
                throw new InvalidDataException("Invalid PCM sound layout.");
            int frames = samples.Length / format.BlockAlign;
            int peak = 0;
            for (int i = 0; i < samples.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(samples, i)));
            if (peak == 0) throw new InvalidDataException("The notification sound contains only silence.");
            // Ignore near-silent tails, while keeping the body and decay of the struck note.
            int threshold = Math.Max(8, peak / 500);
            int first = 0, last = frames - 1;
            while (first < frames && !AudibleFrame(samples, first, format, threshold)) first++;
            while (last > first && !AudibleFrame(samples, last, format, threshold)) last--;
            if (first == frames) throw new InvalidDataException("The notification sound is too quiet.");
            int keptFrames = last - first + 1;
            byte[] result = new byte[keptFrames * format.BlockAlign];
            // Attenuate only; never increase the recording's level or change the output volume.
            double gain = Math.Min(0.65, (0.16 * short.MaxValue) / peak);
            int fadeFrames = Math.Max(1, (int)(format.Rate * 0.003));
            for (int frame = 0; frame < keptFrames; frame++)
            {
                double envelope = Math.Min(1.0, Math.Min((frame + 1.0) / fadeFrames, (keptFrames - frame) / (double)fadeFrames));
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int target = frame * format.BlockAlign + channel * 2;
                    short value = (short)Math.Round(BitConverter.ToInt16(samples, (first + frame) * format.BlockAlign + channel * 2) * gain * envelope);
                    result[target] = (byte)(value & 255); result[target + 1] = (byte)((value >> 8) & 255);
                }
            }
            return result;
        }
        private static bool AudibleFrame(byte[] samples, int frame, WaveFormat format, int threshold)
        {
            for (int channel = 0; channel < format.Channels; channel++)
                if (Math.Abs((int)BitConverter.ToInt16(samples, frame * format.BlockAlign + channel * 2)) > threshold) return true;
            return false;
        }
        private static void Play(int cue, string endpointId)
        {
            WaveFormat format;
            byte[] samples = Samples(cue, out format);
            uint device = endpointId == null ? uint.MaxValue : FindOutput(endpointId);
            IntPtr output;
            Check(waveOutOpen(out output, device, ref format, IntPtr.Zero, IntPtr.Zero, 0));
            IntPtr data = IntPtr.Zero, header = IntPtr.Zero;
            bool prepared = false;
            try
            {
                data = Marshal.AllocHGlobal(samples.Length);
                Marshal.Copy(samples, 0, data, samples.Length);
                header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHeader)));
                Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)samples.Length }, header, false);
                Check(waveOutPrepareHeader(output, header, (uint)Marshal.SizeOf(typeof(WaveHeader))));
                prepared = true;
                Check(waveOutWrite(output, header, (uint)Marshal.SizeOf(typeof(WaveHeader))));
                int flagOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "Flags");
                var timer = Stopwatch.StartNew();
                while ((Marshal.ReadInt32(header, flagOffset) & 1) == 0)
                {
                    if (timer.ElapsedMilliseconds > 4000) throw new TimeoutException("Notification playback timed out.");
                    Thread.Sleep(15);
                }
            }
            finally
            {
                waveOutReset(output);
                if (prepared) waveOutUnprepareHeader(output, header, (uint)Marshal.SizeOf(typeof(WaveHeader)));
                waveOutClose(output);
                if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            }
        }
        internal static uint FindOutput(string endpointId)
        {
            // Map the exact Core Audio endpoint to a waveform device. Never guess by its truncated display name.
            for (uint i = 0; i < waveOutGetNumDevs(); i++)
            {
                IntPtr sizePointer = Marshal.AllocHGlobal(4);
                IntPtr text = IntPtr.Zero;
                try
                {
                    Marshal.WriteInt32(sizePointer, 0);
                    if (waveOutMessage(new IntPtr(i), 0x812, sizePointer, IntPtr.Zero) != 0) continue;
                    int bytes = Marshal.ReadInt32(sizePointer);
                    if (bytes <= 0 || bytes > 65536) continue;
                    text = Marshal.AllocHGlobal(bytes);
                    if (waveOutMessage(new IntPtr(i), 0x811, text, new IntPtr(bytes)) == 0 &&
                        string.Equals(Marshal.PtrToStringUni(text), endpointId, StringComparison.OrdinalIgnoreCase)) return i;
                }
                finally { if (text != IntPtr.Zero) Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(sizePointer); }
            }
            throw new InvalidOperationException("The selected output is not ready for its notification sound.");
        }
        private static void Check(uint result) { if (result != 0) throw new InvalidOperationException("Windows audio playback error " + result + "."); }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct WaveFormat
        { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
        [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll")] private static extern uint waveOutMessage(IntPtr device, uint message, IntPtr parameter1, IntPtr parameter2);
        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
    }
}
