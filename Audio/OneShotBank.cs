using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;

namespace CodexEngine.Audio
{
    /// <summary>Preloaded clips and reusable FAudio voices on FNA's sound device. Owned by the playback thread.</summary>
    public sealed class OneShotBank : IDisposable
    {
        private sealed class VoicePool : IDisposable
        {
            internal readonly SoundEffect FormatSource;
            private readonly IntPtr[] voices;
            private readonly IntPtr output;
            private readonly uint sourceChannels, outputChannels;
            private readonly float[] centerMatrix, panMatrix;
            private int next;

            internal unsafe VoicePool(SoundEffect effect, int capacity, IntPtr output)
            {
                FormatSource = effect;
                var device = SoundEffect.Device();
                this.output = output;
                sourceChannels = effect.channels;
                outputChannels = device.DeviceDetails.OutputFormat.Format.nChannels;
                centerMatrix = new float[sourceChannels * outputChannels];
                panMatrix = new float[centerMatrix.Length];
                voices = new IntPtr[capacity];
                try
                {
                    var send = new FAudio.FAudioSendDescriptor { pOutputVoice = output };
                    var sends = new FAudio.FAudioVoiceSends { SendCount = 1, pSends = (IntPtr)(&send) };
                    for (int i = 0; i < voices.Length; i++)
                        Check(FAudio.FAudio_CreateSourceVoice(device.Handle, out voices[i], effect.formatPtr, 0,
                            FAudio.FAUDIO_DEFAULT_FREQ_RATIO, IntPtr.Zero, (IntPtr)(&sends), IntPtr.Zero));
                    FAudio.FAudioVoice_GetOutputMatrix(voices[0], output, sourceChannels, outputChannels, centerMatrix);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal unsafe void Play(SoundEffect effect, float volume, float speed, float pan)
            {
                // Start searching after the last voice used. Recycle that ring's next voice if all are busy.
                int index = next;
                do
                {
                    FAudio.FAudioSourceVoice_GetState(voices[index], out var state, FAudio.FAUDIO_VOICE_NOSAMPLESPLAYED);
                    if (state.BuffersQueued == 0) break;
                    index = (index + 1) % voices.Length;
                    if (index == next)
                    {
                        Check(FAudio.FAudioSourceVoice_Stop(voices[index], 0, 0));
                        Check(FAudio.FAudioSourceVoice_FlushSourceBuffers(voices[index]));
                        break;
                    }
                } while (true);
                next = (index + 1) % voices.Length;
                IntPtr voice = voices[index];
                fixed (float* matrix = pan == 0f ? centerMatrix : panMatrix)
                {
                    if (pan != 0f)
                        SoundEffectInstance.SetPanMatrixCoefficients(pan, sourceChannels, outputChannels, matrix);
                    Check(FAudio.FAudioVoice_SetOutputMatrix(voice, output, sourceChannels, outputChannels, (IntPtr)matrix, 0));
                }
                Check(FAudio.FAudioVoice_SetVolume(voice, volume, 0));
                Check(FAudio.FAudioSourceVoice_SetFrequencyRatio(voice, speed, 0));
                Check(FAudio.FAudioSourceVoice_SubmitSourceBuffer(voice, ref effect.handle, IntPtr.Zero));
                Check(FAudio.FAudioSourceVoice_Start(voice, 0, 0));
            }

            public void Dispose()
            {
                foreach (var voice in voices)
                    if (voice != IntPtr.Zero) FAudio.FAudioVoice_DestroyVoice(voice);
            }
        }

        private readonly string directory;
        private readonly int voicesPerFormat;
        private readonly uint sampleRate;
        private IntPtr submix;
        private readonly List<VoicePool> pools = new List<VoicePool>();
        private readonly Dictionary<string, (SoundEffect Effect, VoicePool Pool)> clips =
            new Dictionary<string, (SoundEffect, VoicePool)>(StringComparer.OrdinalIgnoreCase);

        public OneShotBank(string directory, int voicesPerFormat)
        {
            if (voicesPerFormat < 1) throw new ArgumentOutOfRangeException(nameof(voicesPerFormat));
            this.directory = directory;
            this.voicesPerFormat = voicesPerFormat;
            var device = SoundEffect.Device();
            var format = device.DeviceDetails.OutputFormat.Format;
            sampleRate = format.nSamplesPerSec;
            Check(FAudio.FAudio_CreateSubmixVoice(device.Handle, out submix, format.nChannels, sampleRate,
                FAudio.FAUDIO_VOICE_USEFILTER, 0, IntPtr.Zero, IntPtr.Zero));
        }

        public void SetLowPass(float cutoffHz) => SetLowPass(submix, sampleRate, cutoffHz);

        public static void SetLowPass(SoundEffectInstance instance, float cutoffHz)
        {
            FAudio.FAudioVoice_GetVoiceDetails(instance.handle, out var details);
            SetLowPass(instance.handle, details.InputSampleRate, cutoffHz);
        }

        private static void SetLowPass(IntPtr voice, uint rate, float cutoffHz)
        {
            float frequency = cutoffHz >= rate / 6f ? FAudio.FAUDIO_DEFAULT_FILTER_FREQUENCY
                : 2f * MathF.Sin(MathF.PI * cutoffHz / rate);
            var filter = new FAudio.FAudioFilterParameters {
                Type = FAudio.FAudioFilterType.FAudioLowPassFilter, Frequency = frequency, OneOverQ = 1f
            };
            Check(FAudio.FAudioVoice_SetFilterParameters(voice, ref filter, 0));
        }

        public void Load(string name)
        {
            if (clips.ContainsKey(name)) return;
            using var stream = File.OpenRead(Path.Combine(directory, name));
            var effect = SoundEffect.FromStream(stream);
            try
            {
                foreach (var pool in pools)
                {
                    if (Format(pool.FormatSource).SequenceEqual(Format(effect)))
                    {
                        clips.Add(name, (effect, pool));
                        return;
                    }
                }
                var newPool = new VoicePool(effect, voicesPerFormat, submix);
                pools.Add(newPool);
                clips.Add(name, (effect, newPool));
            }
            catch
            {
                effect.Dispose();
                throw;
            }
        }

        private static unsafe ReadOnlySpan<byte> Format(SoundEffect effect)
        {
            var format = (FAudio.FAudioWaveFormatEx*)effect.formatPtr;
            return new ReadOnlySpan<byte>(format, sizeof(FAudio.FAudioWaveFormatEx) + format->cbSize);
        }

        public void Play(string name, float volume, float speed, float pan)
        {
            var clip = clips[name];
            clip.Pool.Play(clip.Effect, Math.Clamp(volume, 0f, 1f), Math.Clamp(speed, .5f, 2f), Math.Clamp(pan, -1f, 1f));
        }

        private static void Check(uint result)
        {
            if (result != 0) throw new InvalidOperationException($"FAudio voice operation failed (0x{result:X8}).");
        }

        public void Dispose()
        {
            // The mixer may still be reading the sample memory of any clip in the pool.
            foreach (var pool in pools) pool.Dispose();
            pools.Clear();
            foreach (var clip in clips.Values) clip.Effect.Dispose();
            clips.Clear();
            if (submix != IntPtr.Zero)
            {
                FAudio.FAudioVoice_DestroyVoice(submix);
                submix = IntPtr.Zero;
            }
        }
    }
}
