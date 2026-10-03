using UnityEngine;

namespace NOInfantry
{
    // Procedural footstep sounds (short low-passed noise thumps), so no audio files are shipped or borrowed.
    internal class Footsteps
    {
        private readonly AudioSource source;
        private readonly AudioClip[] clips = new AudioClip[4];
        private float distanceAccum;

        internal Footsteps(GameObject host)
        {
            source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            if (SoundManager.i != null) source.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
            for (int i = 0; i < clips.Length; i++) clips[i] = MakeStep(1234 + i * 97);
        }

        // Call each frame with horizontal speed (m/s); stride length shrinks when crouched/prone.
        internal void Tick(bool grounded, float speed, float stride, float loudness)
        {
            if (!grounded || speed < 0.6f)
            {
                distanceAccum = stride * 0.6f; // first step comes quickly after starting to move
                return;
            }
            distanceAccum += speed * Time.deltaTime;
            if (distanceAccum < stride) return;
            distanceAccum = 0f;
            source.pitch = Random.Range(0.85f, 1.1f);
            source.PlayOneShot(clips[Random.Range(0, clips.Length)], Plugin.FootstepVolume.Value * loudness * Mathf.Clamp01(speed / 4f + 0.4f));
        }

        private static AudioClip MakeStep(int seed)
        {
            const int rate = 44100;
            int n = (int)(rate * 0.12f);
            var data = new float[n];
            var rng = new System.Random(seed);
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float env = Mathf.Exp(-t * 9f) * Mathf.Min(1f, i / (rate * 0.004f));
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += (noise - lp) * 0.08f;       // two one-pole low-pass stages: dull "thud" on dirt/concrete
                lp2 += (lp - lp2) * 0.12f;
                float body = Mathf.Sin(2f * Mathf.PI * 85f * i / rate) * Mathf.Exp(-t * 14f) * 0.35f;
                data[i] = (lp2 * 3.5f + body) * env;
            }
            AudioClip clip = AudioClip.Create($"noi_step_{seed}", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
