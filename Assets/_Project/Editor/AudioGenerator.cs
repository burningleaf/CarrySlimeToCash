using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if !AUDIOGEN_VERIFY
using UnityEditor;
using UnityEngine;
#endif

public static class AudioGenerator
{
    public const int SampleRate = 44100;
    public const int NoiseSeed = 20240613;
    public const string AudioRoot = "Assets/_Project/Audio/SFX";
    public const float MaxDuration = 1.5f;
    public const float NormalizeCeiling = 0.98f;
    public const double SquareDuty = 0.5;
    public const double NoiseLevel = 0.18;
    /// <summary>
    /// 菜单路径（已归位）：原来在没人看得懂的 `Tools/DAIDAI/GenAudio`（"DAIDAI" 是历史遗留名），
    /// 现在收进主菜单「呆呆史莱姆」下，与美术生成器并列。
    /// 批处理入口 `AudioGenerator.BatchGenerateAudio` 与 `菜单 GenerateAudioMenu` 的分工不变。
    /// </summary>
    private const string MenuPath = "Tools/呆呆史莱姆/♪ 生成音效素材";
    private const double DefAttack = 0.008;
    private const double DefDecay = 0.120;
    private const double DefSustain = 0.002;
    private const double DefRelease = 0.015;

#if !AUDIOGEN_VERIFY
    [MenuItem(MenuPath, false, 21)]
    public static void GenerateAudioMenu()
    {
        int n = GenerateAll();
        string msg = "gen " + n + " wav -> " + AudioRoot;
        if (!Application.isBatchMode) EditorUtility.DisplayDialog("生成音效素材", msg, "ok");
    }

    /// <summary>批处理入口（-executeMethod AudioGenerator.BatchGenerateAudio），无对话框。</summary>
    public static void BatchGenerateAudio()
    {
        GenerateAll();
    }
#endif

    private static double[] Render(SoundDef def)
    {
        int n = (int)Math.Round(def.duration * SampleRate);
        double[] buf = new double[n > 0 ? n : 1];
        def.build(new Mix(buf));
        double peak = Normalize(buf);
#if !AUDIOGEN_VERIFY
        if (peak > 1.0) Debug.LogWarning("[AudioGenerator] normalized " + def.fileName + " peak " + peak.ToString("F4"));
#endif
        return buf;
    }

#if !AUDIOGEN_VERIFY
    public static int GenerateAll()
    {
        var defs = BuildDefinitions();
        var report = new StringBuilder();
        int ok = 0, failed = 0;
        double totalSec = 0.0;
        try
        {
            for (int i = 0; i < defs.Count; i++)
            {
                SoundDef def = defs[i];
                EditorUtility.DisplayProgressBar("gen", def.fileName, (float)i / Mathf.Max(1, defs.Count));
                try
                {
                    double[] buf = Render(def);
                    WriteWav(AudioRoot + "/" + def.category + "/" + def.fileName, buf);
                    double realSec = (double)buf.Length / SampleRate;
                    totalSec += realSec; ok++;
                    report.AppendLine("  " + def.category + "/" + def.fileName + " " + realSec.ToString("F3") + "s peak " + PeakOf(buf).ToString("F3"));
                }
                catch (Exception e) { failed++; Debug.LogError("[AudioGenerator] fail " + def.fileName + "\n" + e); }
            }
        }
        finally { EditorUtility.ClearProgressBar(); AssetDatabase.Refresh(); }
        Debug.Log("[AudioGenerator] ok " + ok + " fail " + failed + " total " + totalSec.ToString("F2") + "s\n" + report);
        return ok;
    }
#endif

    private class SoundDef
    {
        public string category;
        public string fileName;
        public float duration;
        public Action<Mix> build;
        public SoundDef(string category, string fileName, float duration, Action<Mix> build)
        {
            this.category = category; this.fileName = fileName; this.duration = duration; this.build = build;
        }
    }

    private static List<SoundDef> BuildDefinitions()
    {
        var list = new List<SoundDef>(32);

        // 参数顺序：波形, 发声时长, 起始频率, 结束频率, 起始时间(秒), 振幅,
        //           起音秒, 衰减秒, 音量门, 收尾淡出秒, 噪声低通Hz, 颤音Hz, 颤音音分, 环形调制Hz, 环形调制深度, 延迟秒
        // 衰减都是指数衰减：时间常数 = 衰减秒 / 3（即 衰减秒 内衰到约 -26dB）。
        // 收尾淡出必须留够，否则音尾被硬切 -> "啪"。

        // 跳：方波 420→880 上滑，干脆的"啾"
        list.Add(new SoundDef("Player", "SFX_Player_Jump.wav", 0.12f, b =>
        { b.Note(Wave.Square, 0.12, 420, 880, 0.0, 0.45, 0.004, 0.020, 0.0, 0.030); }));

        // 落地：低频正弦 180→90 下滑 + 一点噪声，短促沉闷的"咚"
        list.Add(new SoundDef("Player", "SFX_Player_Land.wav", 0.10f, b =>
        {
            b.Note(Wave.Sine, 0.10, 180, 90, 0.0, 0.50, 0.004, 0.016, 0.0, 0.025);
            b.Note(Wave.Noise, 0.10, 1000, 1000, 0.0, 0.20, 0.003, 0.008, 0.0, 0.020, 900);
        }));

        // 死亡：方波 500→110 长下滑，末尾轻微颤音，失落感
        list.Add(new SoundDef("Player", "SFX_Player_Death.wav", 0.45f, b =>
        {
            b.Note(Wave.Square, 0.45, 500, 110, 0.0, 0.55, 0.010, 0.130, 0.0, 0.035, 0, 5.5, 55);
            b.Note(Wave.Sine, 0.45, 250, 55, 0.0, 0.30, 0.010, 0.120, 0.0, 0.035);
            b.Note(Wave.Noise, 0.45, 500, 500, 0.02, 0.10, 0.060, 0.080, 0.0, 0.030, 2500);
        }));

        // 抓取：三角 300→520，很短的"嗒"
        list.Add(new SoundDef("Player", "SFX_Player_Grab.wav", 0.08f, b =>
        { b.Note(Wave.Triangle, 0.08, 300, 520, 0.0, 0.60, 0.006, 0.010, 0.0, 0.025); }));

        // 放置：三角 520→300，和 Grab 反向
        list.Add(new SoundDef("Player", "SFX_Player_Place.wav", 0.09f, b =>
        { b.Note(Wave.Triangle, 0.09, 520, 300, 0.0, 0.50, 0.006, 0.012, 0.0, 0.028); }));

        // 投掷：噪声 + 正弦 700→200 下滑，"咻"
        list.Add(new SoundDef("Player", "SFX_Player_Throw.wav", 0.18f, b =>
        {
            b.Note(Wave.Noise, 0.18, 1000, 1000, 0.0, 0.35, 0.030, 0.020, 0.0, 0.070, 3800);
            b.Note(Wave.Sine, 0.18, 700, 200, 0.0, 0.45, 0.020, 0.025, 0.0, 0.060);
        }));

        // 吹哨：两个正弦叠（1800 + 2400），快速颤音 6Hz
        list.Add(new SoundDef("Player", "SFX_Player_Whistle.wav", 0.30f, b =>
        {
            b.Note(Wave.Sine, 0.30, 1800, 1800, 0.0, 0.50, 0.030, 0.070, 0.0, 0.080, 0, 6.0, 35);
            b.Note(Wave.Sine, 0.30, 2400, 2400, 0.0, 0.25, 0.030, 0.060, 0.0, 0.080, 0, 6.0, 35);
        }));

        // 路径点：正弦 880 短音 + 一次泛音 1320
        list.Add(new SoundDef("Player", "SFX_Player_Waypoint.wav", 0.10f, b =>
        {
            b.Note(Wave.Sine, 0.10, 880, 880, 0.0, 0.45, 0.004, 0.012, 0.0, 0.030);
            b.Note(Wave.Sine, 0.10, 1320, 1320, 0.0, 0.28, 0.004, 0.010, 0.0, 0.025);
        }));

        // 通关结算：三个下行短音 900→700→500
        list.Add(new SoundDef("Player", "SFX_Player_Clear.wav", 0.20f, b =>
        {
            b.Note(Wave.Square, 0.20, 900, 900, 0.0, 0.32, 0.004, 0.012, 0.0, 0.025);
            b.Note(Wave.Square, 0.20, 700, 700, 0.0, 0.32, 0.004, 0.012, 0.0, 0.025, 0, 0, 0, 0, 0, 0.07);
            b.Note(Wave.Square, 0.20, 500, 500, 0.0, 0.34, 0.004, 0.012, 0.0, 0.030, 0, 0, 0, 0, 0, 0.14);
        }));

        // 受伤：方波 300→160 下滑 + 噪声，黏糊糊的痛
        list.Add(new SoundDef("Slime", "SFX_Slime_Hurt.wav", 0.16f, b =>
        {
            b.Note(Wave.Square, 0.16, 300, 160, 0.0, 0.42, 0.005, 0.018, 0.0, 0.045);
            b.Note(Wave.Noise, 0.16, 900, 900, 0.0, 0.22, 0.005, 0.010, 0.0, 0.030, 2200);
        }));

        // 治疗：三个上行正弦 500→750→1000，明亮
        list.Add(new SoundDef("Slime", "SFX_Slime_Heal.wav", 0.30f, b =>
        {
            b.Note(Wave.Sine, 0.30, 500, 500, 0.0, 0.36, 0.005, 0.025, 0.0, 0.040);
            b.Note(Wave.Sine, 0.30, 750, 750, 0.0, 0.36, 0.005, 0.025, 0.0, 0.040, 0, 0, 0, 0, 0, 0.10);
            b.Note(Wave.Sine, 0.30, 1000, 1000, 0.0, 0.40, 0.005, 0.025, 0.0, 0.050, 0, 0, 0, 0, 0, 0.20);
        }));

        // 模式切换：两声短促方波 660、880
        list.Add(new SoundDef("Slime", "SFX_Slime_Mode.wav", 0.12f, b =>
        {
            b.Note(Wave.Square, 0.12, 660, 660, 0.0, 0.38, 0.004, 0.012, 0.0, 0.025);
            b.Note(Wave.Square, 0.12, 880, 880, 0.0, 0.38, 0.004, 0.012, 0.0, 0.025, 0, 0, 0, 0, 0, 0.06);
        }));

        // 死亡：正弦 400→80 长下滑 + 噪声渐入
        list.Add(new SoundDef("Slime", "SFX_Slime_Death.wav", 0.50f, b =>
        {
            b.Note(Wave.Sine, 0.50, 400, 80, 0.0, 0.55, 0.010, 0.130, 0.0, 0.050);
            b.Note(Wave.Noise, 0.50, 900, 900, 0.10, 0.22, 0.120, 0.060, 0.0, 0.040, 1200);
        }));

        // 金币：1320 正弦 0.06s，紧接 1760 正弦 0.10s，都快速衰减
        list.Add(new SoundDef("Item", "SFX_Item_Coin.wav", 0.16f, b =>
        {
            b.Note(Wave.Sine, 0.16, 1320, 1320, 0.0, 0.60, 0.004, 0.018, 0.0, 0.060);
            b.Note(Wave.Sine, 0.16, 1760, 1760, 0.0, 0.55, 0.004, 0.022, 0.0, 0.050, 0, 0, 0, 0, 0, 0.06);
        }));

        // 能量球：上行琶音 660→880→1320，柔和三角
        list.Add(new SoundDef("Item", "SFX_Item_Orb.wav", 0.22f, b =>
        {
            b.Note(Wave.Triangle, 0.22, 660, 660, 0.0, 0.40, 0.005, 0.030, 0.0, 0.030);
            b.Note(Wave.Triangle, 0.22, 880, 880, 0.0, 0.40, 0.005, 0.030, 0.0, 0.030, 0, 0, 0, 0, 0, 0.07);
            b.Note(Wave.Triangle, 0.22, 1320, 1320, 0.0, 0.44, 0.005, 0.030, 0.0, 0.040, 0, 0, 0, 0, 0, 0.14);
        }));

        // 踩板按下：低频方波 220→150，很短的"咔"
        list.Add(new SoundDef("Level", "SFX_Plate_Press.wav", 0.07f, b =>
        {
            b.Note(Wave.Square, 0.07, 220, 150, 0.0, 0.45, 0.004, 0.010, 0.0, 0.020);
            b.Note(Wave.Noise, 0.07, 1000, 1000, 0.0, 0.10, 0.002, 0.006, 0.0, 0.015, 2500);
        }));

        // 踩板松开：150→220 上行版
        list.Add(new SoundDef("Level", "SFX_Plate_Release.wav", 0.07f, b =>
        {
            b.Note(Wave.Square, 0.07, 150, 220, 0.0, 0.45, 0.004, 0.010, 0.0, 0.020);
            b.Note(Wave.Noise, 0.07, 1000, 1000, 0.0, 0.08, 0.002, 0.006, 0.0, 0.015, 2500);
        }));

        // 存档点：上行双音 784 / 1175，每次 0.15s，带轻微混响感（衰减更慢的副本）
        list.Add(new SoundDef("Level", "SFX_Level_Checkpoint.wav", 0.35f, b =>
        {
            b.Note(Wave.Sine, 0.35, 784, 784, 0.0, 0.45, 0.005, 0.040, 0.0, 0.040);
            b.Note(Wave.Sine, 0.35, 1175, 1175, 0.0, 0.45, 0.005, 0.040, 0.0, 0.040, 0, 0, 0, 0, 0, 0.15);
            // 混响副本：延迟 0.12s、电平更低、衰减更慢
            b.Note(Wave.Sine, 0.35, 784, 784, 0.0, 0.16, 0.005, 0.050, 0.0, 0.060, 0, 0, 0, 0, 0, 0.12);
            b.Note(Wave.Sine, 0.35, 1175, 1175, 0.0, 0.14, 0.005, 0.050, 0.0, 0.060, 0, 0, 0, 0, 0, 0.22);
        }));

        // 通关：C-E-G-C（523/659/784/1047），每音 0.28s，最后一个音拉长并叠高八度
        list.Add(new SoundDef("Level", "SFX_Level_Goal.wav", 1.20f, b =>
        {
            b.Note(Wave.Sine, 1.20, 523, 523, 0.0, 0.42, 0.005, 0.060, 0.0, 0.030);
            b.Note(Wave.Sine, 1.20, 659, 659, 0.0, 0.42, 0.005, 0.060, 0.0, 0.030, 0, 0, 0, 0, 0, 0.28);
            b.Note(Wave.Sine, 1.20, 784, 784, 0.0, 0.42, 0.005, 0.060, 0.0, 0.030, 0, 0, 0, 0, 0, 0.56);
            b.Note(Wave.Sine, 1.20, 1047, 1047, 0.0, 0.48, 0.005, 0.120, 0.0, 0.480, 0, 0, 0, 0, 0, 0.66);
            b.Note(Wave.Sine, 1.20, 2093, 2093, 0.0, 0.20, 0.005, 0.280, 0.0, 0.480, 0, 0, 0, 0, 0, 0.66);
        }));

        // 点击：1200 正弦极短，快速衰减
        list.Add(new SoundDef("UI", "SFX_UI_Click.wav", 0.06f, b =>
        { b.Note(Wave.Sine, 0.06, 1200, 1200, 0.0, 0.60, 0.003, 0.008, 0.0, 0.018); }));

        // 打开面板：600→1000 上行三角
        list.Add(new SoundDef("UI", "SFX_UI_Open.wav", 0.18f, b =>
        { b.Note(Wave.Triangle, 0.18, 600, 1000, 0.0, 0.50, 0.008, 0.030, 0.0, 0.050); }));

        // 切换：800 方波短音
        list.Add(new SoundDef("UI", "SFX_UI_Switch.wav", 0.08f, b =>
        { b.Note(Wave.Square, 0.08, 800, 800, 0.0, 0.42, 0.004, 0.010, 0.0, 0.022); }));

        // 锁定（UI 里把关卡/按钮画成灰的）：一声闷的短音，"按不动"
        list.Add(new SoundDef("UI", "SFX_UI_Locked.wav", 0.10f, b =>
        {
            b.Note(Wave.Square, 0.10, 260, 180, 0.0, 0.34, 0.005, 0.014, 0.0, 0.035);
            b.Note(Wave.Noise, 0.10, 800, 800, 0.0, 0.12, 0.003, 0.008, 0.0, 0.025, 1500);
        }));

        // 报错：两声下行方波 300、200
        list.Add(new SoundDef("UI", "SFX_UI_Error.wav", 0.20f, b =>
        {
            b.Note(Wave.Square, 0.20, 300, 300, 0.0, 0.40, 0.004, 0.014, 0.0, 0.030);
            b.Note(Wave.Square, 0.20, 200, 200, 0.0, 0.40, 0.004, 0.014, 0.0, 0.030, 0, 0, 0, 0, 0, 0.09);
        }));

        return list;
    }
    private enum Wave { Sine, Square, Triangle, Noise }

    private class Voice
    {
        public Wave wave;
        public double startSec, durSec, f1, f2, amp;
        public double attack, decay, sustain, release;
        public double noiseCutoff, vibratoHz, vibratoCents, ringHz, ringDepth;
    }

    private class Mix
    {
        public readonly double[] data;
        public Mix(double[] data) { this.data = data; }

        public void Note(Wave wave, double durSec, double f1, double f2, double startSec, double amp,
                         double attack = DefAttack, double decay = DefDecay, double sustain = DefSustain,
                         double release = DefRelease, double noiseCutoff = 0.0, double vibratoHz = 0.0,
                         double vibratoCents = 0.0, double ringHz = 0.0, double ringDepth = 0.0,
                         double delaySec = 0.0)
        {
            var v = new Voice();
            v.wave = wave; v.durSec = durSec; v.f1 = f1; v.f2 = f2; v.amp = amp; v.startSec = startSec;
            if (delaySec > 0.0) v.startSec = v.startSec + delaySec;
            v.attack = attack; v.decay = decay; v.sustain = sustain; v.release = release;
            v.noiseCutoff = noiseCutoff; v.vibratoHz = vibratoHz; v.vibratoCents = vibratoCents;
            v.ringHz = ringHz; v.ringDepth = ringDepth;
            Render(v);
        }

        private void Render(Voice v)
        {
            int start = (int)Math.Round(v.startSec * SampleRate);
            int count = (int)Math.Round(v.durSec * SampleRate);
            if (count <= 0 || start >= data.Length) return;
            int end = Math.Min(data.Length, start + count);
            int len = end - start;
            if (len <= 0) return;

            bool useNoise = v.wave == Wave.Noise;
            int noiseOffset = (int)((start * 7919L) % NoiseTable.Length);
            double phase = 0.0, lp = 0.0, alpha = 0.0;
            if (useNoise && v.noiseCutoff > 0.0)
                alpha = 1.0 - Math.Exp(-2.0 * Math.PI * Math.Min(v.noiseCutoff, SampleRate * 0.45) / SampleRate);

            for (int i = 0; i < len; i++)
            {
                double t = (double)i / SampleRate;
                double u = v.durSec > 0.0 ? t / v.durSec : 1.0;
                if (u > 1.0) u = 1.0;

                double env = Envelope(v, t);

                double f = v.f1 + (v.f2 - v.f1) * u;
                if (v.vibratoHz > 0.0 && v.vibratoCents > 0.0)
                    f *= Math.Pow(2.0, (v.vibratoCents / 1200.0) * Math.Sin(2.0 * Math.PI * v.vibratoHz * t));
                if (f < 0.0) f = 0.0;
                if (f > SampleRate * 0.49) f = SampleRate * 0.49;

                double s;
                switch (v.wave)
                {
                    case Wave.Sine: s = Math.Sin(phase); break;
                    case Wave.Square: s = (phase / (2.0 * Math.PI)) % 1.0 < SquareDuty ? 1.0 : -1.0; break;
                    case Wave.Triangle: s = 1.0 - 4.0 * Math.Abs(((phase / (2.0 * Math.PI)) % 1.0) - 0.5); break;
                    default:
                        double raw = NoiseTable[(noiseOffset + i) % NoiseTable.Length];
                        if (alpha > 0.0) { lp += alpha * (raw - lp); s = lp * 3.0; }
                        else s = raw;
                        break;
                }

                phase += 2.0 * Math.PI * f / SampleRate;
                if (phase > 1.0e7) phase -= 1.0e7 * Math.Floor(phase / 1.0e7);

                double a = v.amp * env;
                if (useNoise) a *= NoiseLevel;
                if (v.ringHz > 0.0 && v.ringDepth > 0.0)
                    s *= (1.0 - v.ringDepth) + v.ringDepth * Math.Sin(2.0 * Math.PI * v.ringHz * t);

                data[start + i] += s * a;
            }
        }

        private static double Envelope(Voice v, double t)
        {
            double dur = v.durSec;
            if (dur <= 0.0) return 0.0;
            double rel = v.release > 0.0 ? v.release : 0.0;
            if (rel > dur) rel = dur * 0.5;

            double env;
            if (t < v.attack && v.attack > 0.0)
            {
                env = t / v.attack;
            }
            else
            {
                double td = t - v.attack;
                if (td < 0.0) td = 0.0;
                double gate = v.sustain < 0.0 ? 0.0 : (v.sustain > 1.0 ? 1.0 : v.sustain);
                env = v.decay > 0.0 ? gate + (1.0 - gate) * Math.Exp(-3.0 * td / v.decay) : 1.0;
            }

            if (rel > 0.0 && t > dur - rel)
            {
                double k = (dur - t) / rel;
                if (k < 0.0) k = 0.0;
                if (k > 1.0) k = 1.0;
                env *= k;
            }

            if (env < 0.0) env = 0.0;
            if (env > 1.0) env = 1.0;
            return env;
        }
    }

    private static double[] noiseTable;
    private static double[] NoiseTable
    {
        get
        {
            if (noiseTable == null)
            {
                var rng = new System.Random(NoiseSeed);
                noiseTable = new double[SampleRate];
                for (int i = 0; i < noiseTable.Length; i++) noiseTable[i] = rng.NextDouble() * 2.0 - 1.0;
            }
            return noiseTable;
        }
    }

    private static double Normalize(double[] data)
    {
        double peak = PeakOf(data);
        if (peak <= 1.0) return peak;
        double k = NormalizeCeiling / peak;
        for (int i = 0; i < data.Length; i++) data[i] *= k;
        return peak;
    }

    private static double PeakOf(double[] data)
    {
        double peak = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            double a = data[i] < 0.0 ? -data[i] : data[i];
            if (a > peak) peak = a;
        }
        return peak;
    }

#if !AUDIOGEN_VERIFY
    private static void WriteWav(string assetPath, double[] samples)
    {
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        string dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        int dataBytes = samples.Length * 2;
        var bytes = new byte[44 + dataBytes];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes(36 + dataBytes).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(SampleRate).CopyTo(bytes, 24);
        BitConverter.GetBytes(SampleRate * 2).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BitConverter.GetBytes(dataBytes).CopyTo(bytes, 40);

        for (int i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            if (v > 1.0) v = 1.0;
            if (v < -1.0) v = -1.0;
            short q = (short)Math.Round(v * 32767.0);
            bytes[44 + i * 2] = (byte)(q & 0xFF);
            bytes[44 + i * 2 + 1] = (byte)((q >> 8) & 0xFF);
        }

        if (!BitConverter.IsLittleEndian)
            Debug.LogError("[AudioGenerator] big endian machine, wav bytes need swapping: " + assetPath);

        File.WriteAllBytes(fullPath, bytes);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        ApplyAudioImportSettings(assetPath);
    }

    private static void ApplyAudioImportSettings(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
        if (importer == null)
        {
            Debug.LogWarning("[AudioGenerator] no AudioImporter: " + assetPath);
            return;
        }

        AudioImporterSampleSettings s = importer.defaultSampleSettings;
        s.loadType = AudioClipLoadType.DecompressOnLoad;
        s.compressionFormat = AudioCompressionFormat.Vorbis;
        s.quality = 0.7f;
        // ⚠ Unity 2022.3：preloadAudioData 已经挪进 SampleSettings，
        //   旧的 AudioImporter.preloadAudioData 被标成 [Obsolete(..., true)] —— 是**硬错误**（CS0619），
        //   不是警告。写在外面会整个工程编译不过（这里实测踩过）。
        s.preloadAudioData = true;
        importer.defaultSampleSettings = s;

        importer.forceToMono = true;
        importer.loadInBackground = false;

        importer.SaveAndReimport();
    }
#endif
}