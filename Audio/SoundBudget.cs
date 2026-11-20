using System;
using System.Collections.Generic;
using System.Numerics;

namespace CodexEngine.Audio
{
    /// <summary>
    /// How one kind of transient sound is budgeted (Griefborn's TransientAudioRuntimeConfig profile): at most
    /// <see cref="MaxConcurrent"/> at once, none heard beyond <see cref="MaxDistance"/>, a burst of the same merge group within
    /// <see cref="MergeRadius"/> and <see cref="MergeWindow"/> sounding once, a lower <see cref="Priority"/> number winning a
    /// voice, and every volume scaled by <see cref="Volume"/>.
    /// </summary>
    public sealed record SoundCategory(string Name, int MaxConcurrent, float MaxDistance, float MergeRadius, float MergeWindow,
        int Priority, float Volume)
    {
        public static readonly SoundCategory EnemyVocal = new("EnemyVocal", 4, 40, 3, .1f, 168, .85f);
        public static readonly SoundCategory EnemyAttack = new("EnemyAttack", 4, 28, 3, .08f, 144, .9f);
        public static readonly SoundCategory WeaponImpact = new("WeaponImpact", 4, 30, 3, .08f, 152, .9f);
        public static readonly SoundCategory Destruction = new("Destruction", 3, 40, 6, .12f, 136, 1);
        public static readonly SoundCategory MeteorFlyby = new("MeteorFlyby", 3, 40, 6, .08f, 120, 1);
        public static readonly SoundCategory MeteorExplosion = new("MeteorExplosion", 4, 100, 6, .08f, 104, 1);
        public static readonly SoundCategory General = new("General", 4, 40, 3, .08f, 128, 1);
        public static readonly SoundCategory UI = new("UI", 4, 40, 0, 0, 32, 1);
        public static readonly SoundCategory Player = new("Player", 4, 40, 0, 0, 64, 1);
        public static readonly SoundCategory WeaponShot = new("WeaponShot", 4, 40, 3, .08f, 80, 1);
        /// <summary>Griefborn's enemy vocal priorities by cue: idle chatter yields to hurt cries, which yield to deaths.</summary>
        public const int IdlePriority = 168, HitPriority = 120, DeathPriority = 112;
    }

    /// <summary>
    /// Griefborn's transient audio budget (AudioVirtualization: TransientAudioAdmission and TransientAudioRouter) over
    /// <see cref="OneShotBank"/> voices. A request is accepted or refused at once and never queued:
    /// a positioned sound at or past its category's distance is refused; one that repeats a recent burst of its merge group
    /// nearby is refused; otherwise it takes a slot while its category and the whole budget have room, or replaces the worst
    /// voice it may (same category when the category is full, any when the budget is): one with a higher priority number,
    /// or the same number but more than 0.8 times as far away. Replaced voices, and positioned ones the listener has left
    /// behind, fade out over 0.08 s. A slot is held for the clip's length at its speed plus 0.05 s.
    /// </summary>
    public sealed class SoundBudget
    {
        private struct Slot
        {
            public OneShotBank Bank;
            public OneShotBank.Voice Voice;
            public SoundCategory Category;
            public int Priority;
            public Vector3 Position;
            public bool Spatial, Retiring;
            public float Volume, Expires, RetiredAt;
        }
        private struct Burst
        {
            public SoundCategory Category;
            public int Group;
            public Vector3 Position;
            public bool Spatial;
            public float Time;
        }

        /// <summary>MaxActiveTransients: voices the budget lets sound at once.</summary>
        public int MaxActive { get; set; } = 12;
        /// <summary>The listener (the camera), for distance culling and stealing.</summary>
        public Vector3 Listener { get; set; }
        /// <summary>Requests refused since the budget was made, for diagnostics.</summary>
        public int Refused { get; private set; }
        public int Active { get; private set; }

        private const float RetireFade = .08f, ExpiryMargin = .05f, StealDistanceRatio = .64f;
        private readonly List<Slot> slots = new List<Slot>();
        private readonly Burst[] bursts = new Burst[64];
        private int burstNext;
        private float clock;

        /// <summary>Advances the budget's clock: ended voices free their slots, retiring ones fade, out-of-range ones retire.</summary>
        public void Tick(float dt)
        {
            clock += Math.Max(0, dt);
            int active = 0;
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                var s = slots[i];
                if (s.Retiring)
                {
                    float left = 1 - (clock - s.RetiredAt) / RetireFade;
                    if (left <= 0 || !s.Bank.IsPlaying(s.Voice)) { s.Bank.Stop(s.Voice); slots.RemoveAt(i); continue; }
                    s.Bank.SetVolume(s.Voice, s.Volume * left);
                    continue;
                }
                if (clock >= s.Expires || !s.Bank.IsPlaying(s.Voice)) { slots.RemoveAt(i); continue; }
                if (s.Spatial && Vector3.DistanceSquared(s.Position, Listener) >= s.Category.MaxDistance * s.Category.MaxDistance)
                {
                    Retire(i);
                    continue;
                }
                active++;
            }
            Active = active;
        }

        /// <summary>
        /// Plays <paramref name="clip"/> from <paramref name="bank"/> if the budget admits it. <paramref name="volume"/> is the
        /// caller's gain (attenuation included); the category's volume multiplies it. <paramref name="position"/> null is a
        /// non-positioned (2D) sound. A merge group of 0 never merges. A <paramref name="priority"/> below 0 takes the category's.
        /// </summary>
        public bool Play(OneShotBank bank, string clip, SoundCategory category, float volume, float speed, float pan,
            Vector3? position = null, int mergeGroup = 0, int priority = -1)
        {
            if (bank == null || clip == null || volume <= 0) { Refused++; return false; }
            int rank = priority >= 0 ? priority : category.Priority;
            bool spatial = position.HasValue;
            var at = position ?? Listener;
            float distanceSq = spatial ? Vector3.DistanceSquared(at, Listener) : 0;
            if (spatial && distanceSq >= category.MaxDistance * category.MaxDistance) { Refused++; return false; }
            if (Merges(category, mergeGroup, at, spatial)) { Refused++; return false; }
            int inCategory = 0, total = 0;
            foreach (var s in slots)
            {
                if (s.Retiring) continue;
                total++;
                if (s.Category == category) inCategory++;
            }
            bool categoryFull = inCategory >= category.MaxConcurrent, budgetFull = total >= MaxActive;
            if (categoryFull || budgetFull)
            {
                int victim = Victim(categoryFull ? category : null, rank, distanceSq);
                if (victim < 0) { Refused++; return false; }
                Retire(victim);
            }
            float gain = Math.Clamp(volume * category.Volume, 0, 1);
            var voice = bank.Play(clip, gain, speed, pan);
            slots.Add(new Slot {
                Bank = bank, Voice = voice, Category = category, Priority = rank, Position = at, Spatial = spatial, Volume = gain,
                Expires = clock + bank.Length(clip) / Math.Max(.01f, Math.Abs(Math.Clamp(speed, .5f, 2f))) + ExpiryMargin
            });
            if (mergeGroup != 0 && category.MergeWindow > 0)
            {
                bursts[burstNext] = new Burst { Category = category, Group = mergeGroup, Position = at, Spatial = spatial, Time = clock };
                burstNext = (burstNext + 1) % bursts.Length;
            }
            return true;
        }

        /// <summary>Stops every voice the budget holds (a round ending).</summary>
        public void Clear()
        {
            foreach (var s in slots) s.Bank.Stop(s.Voice);
            slots.Clear();
            Array.Clear(bursts, 0, bursts.Length);
            Active = 0;
        }

        private bool Merges(SoundCategory category, int group, Vector3 at, bool spatial)
        {
            if (group == 0 || category.MergeWindow <= 0) return false;
            float radiusSq = category.MergeRadius * category.MergeRadius;
            foreach (var b in bursts)
            {
                if (b.Category != category || b.Group != group || b.Spatial != spatial) continue;
                if (clock - b.Time >= category.MergeWindow) continue;
                if (!spatial || Vector3.DistanceSquared(b.Position, at) <= radiusSq) return true;
            }
            return false;
        }

        /// <summary>The worst replaceable voice (of <paramref name="only"/> when given): the highest priority number, then the farthest.</summary>
        private int Victim(SoundCategory only, int rank, float distanceSq)
        {
            int best = -1;
            int bestRank = int.MinValue;
            float bestDistance = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s.Retiring || only != null && s.Category != only) continue;
                float sd = s.Spatial ? Vector3.DistanceSquared(s.Position, Listener) : 0;
                bool replaceable = rank < s.Priority || rank == s.Priority && distanceSq < sd * StealDistanceRatio;
                if (!replaceable) continue;
                if (s.Priority > bestRank || s.Priority == bestRank && sd > bestDistance)
                {
                    best = i; bestRank = s.Priority; bestDistance = sd;
                }
            }
            return best;
        }

        private void Retire(int index)
        {
            var s = slots[index];
            if (s.Retiring) return;
            s.Retiring = true;
            s.RetiredAt = clock;
            slots[index] = s;
        }
    }
}
