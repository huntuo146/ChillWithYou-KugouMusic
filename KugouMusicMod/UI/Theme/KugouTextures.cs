using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChillWithYou.KugouMusic.UI.Theme
{
    /// <summary>
    /// 程序化 UI 纹理工厂与缓存池（全部标记 HideAndDontSave，在插件退出时统一 DisposeAll）。
    /// 生成：品牌渐变、柔高光、径向辉光、磨砂噪点、Gaussian 阴影、SDF 圆角/描边蒙版、黑胶唱片、羽化柔雾等。
    /// </summary>
    internal static class KugouTextures
    {
        private static Texture2D _brandGradient;
        private static Texture2D _brandGradientSoft;
        private static Texture2D _topHighlight;
        private static Texture2D _radialGlow;
        private static Texture2D _noiseTile;
        private static Texture2D _featherVeil;
        private static Texture2D _vinylDisc;
        private static Texture2D _scrollThumbMask;
        private static Texture2D _whiteMask;

        private static readonly Dictionary<int, Texture2D> FillMasks = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<long, Texture2D> StrokeMasks = new Dictionary<long, Texture2D>();
        private static readonly Dictionary<long, Texture2D> ShadowMasks = new Dictionary<long, Texture2D>();
        private static readonly Dictionary<int, Texture2D> CircleMasks = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<long, Texture2D> GradientCapsuleCache = new Dictionary<long, Texture2D>();
        private static readonly Queue<long> GradientCapsuleOrder = new Queue<long>();
        private static readonly Dictionary<int, Texture2D> MonogramCache = new Dictionary<int, Texture2D>();
        private static readonly List<Sprite> CreatedSprites = new List<Sprite>();

        public static Texture2D White
        {
            get { return Texture2D.whiteTexture; }
        }

        public static Texture2D BrandGradient
        {
            get
            {
                if (_brandGradient == null)
                {
                    _brandGradient = CreateHorizontalGradient(
                        256,
                        KugouTheme.Colors.AccentSky,
                        KugouTheme.Colors.AccentLavender,
                        KugouTheme.Colors.AccentCoral);
                }

                return _brandGradient;
            }
        }

        public static Texture2D BrandGradientSoft
        {
            get
            {
                if (_brandGradientSoft == null)
                {
                    Color c0 = KugouTheme.WithAlpha(KugouTheme.Colors.AccentSky, 0.24f);
                    Color c1 = KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.24f);
                    _brandGradientSoft = CreateHorizontalGradient(128, c0, c1, c1);
                }

                return _brandGradientSoft;
            }
        }

        public static Texture2D TopHighlight
        {
            get
            {
                if (_topHighlight == null)
                {
                    var tex = CreateTex(1, 64, TextureWrapMode.Clamp);
                    var px = new Color32[64];
                    // Unity 纹理 y=0 在底部、y=h-1 在顶部
                    for (int y = 0; y < 64; y++)
                    {
                        float topT = y / 63f; // 0=bottom, 1=top
                        float a = Mathf.Pow(topT, 1.6f) * 0.055f;
                        px[y] = new Color(1f, 1f, 1f, a);
                    }

                    tex.SetPixels32(px);
                    tex.Apply(false, true);
                    _topHighlight = tex;
                }

                return _topHighlight;
            }
        }

        public static Texture2D RadialGlow
        {
            get
            {
                if (_radialGlow == null)
                {
                    const int size = 128;
                    var tex = CreateTex(size, size, TextureWrapMode.Clamp);
                    var px = new Color32[size * size];
                    float half = (size - 1) * 0.5f;
                    for (int y = 0; y < size; y++)
                    {
                        float dy = (y - half) / half;
                        for (int x = 0; x < size; x++)
                        {
                            float dx = (x - half) / half;
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            float falloff = Mathf.Clamp01(1f - d);
                            float a = falloff * falloff * (3f - 2f * falloff);
                            px[y * size + x] = new Color(1f, 1f, 1f, a);
                        }
                    }

                    tex.SetPixels32(px);
                    tex.Apply(false, true);
                    _radialGlow = tex;
                }

                return _radialGlow;
            }
        }

        public static Texture2D NoiseTile
        {
            get
            {
                if (_noiseTile == null)
                {
                    const int size = 128;
                    var tex = CreateTex(size, size, TextureWrapMode.Repeat);
                    var px = new Color32[size * size];
                    uint state = 0x6D2B79F5u;
                    for (int i = 0; i < px.Length; i++)
                    {
                        state ^= state << 13;
                        state ^= state >> 17;
                        state ^= state << 5;
                        float rnd = (state & 0xFF) / 255f;
                        byte shade = (byte)(190 + (int)(rnd * 65f));
                        byte alpha = (byte)(rnd * 7.5f); // 约 0 ~ 0.029 柔细颗粒
                        px[i] = new Color32(shade, shade, 255, alpha);
                    }

                    tex.SetPixels32(px);
                    tex.Apply(false, true);
                    _noiseTile = tex;
                }

                return _noiseTile;
            }
        }

        public static Texture2D FeatherVeil
        {
            get
            {
                if (_featherVeil == null)
                {
                    const int w = 256;
                    const int h = 64;
                    var tex = CreateTex(w, h, TextureWrapMode.Clamp);
                    var px = new Color32[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        float ny = Mathf.Abs((y / (float)(h - 1)) * 2f - 1f);
                        float vy = SmoothStep(1f, 0.55f, ny);
                        for (int x = 0; x < w; x++)
                        {
                            float nx = Mathf.Abs((x / (float)(w - 1)) * 2f - 1f);
                            float vx = SmoothStep(1f, 0.72f, nx);
                            float a = vx * vy;
                            px[y * w + x] = new Color(1f, 1f, 1f, a);
                        }
                    }

                    tex.SetPixels32(px);
                    tex.Apply(false, true);
                    _featherVeil = tex;
                }

                return _featherVeil;
            }
        }

        public static Texture2D VinylDisc
        {
            get
            {
                if (_vinylDisc == null)
                {
                    _vinylDisc = CreateVinylDiscTexture(128);
                }

                return _vinylDisc;
            }
        }

        public static Texture2D ScrollThumbMask
        {
            get
            {
                if (_scrollThumbMask == null)
                {
                    _scrollThumbMask = GetRoundedFillMask(3);
                }

                return _scrollThumbMask;
            }
        }

        public static Texture2D SolidWhiteMask
        {
            get
            {
                if (_whiteMask == null)
                {
                    var tex = CreateTex(2, 2, TextureWrapMode.Clamp);
                    var c = new Color32(255, 255, 255, 255);
                    tex.SetPixels32(new[] { c, c, c, c });
                    tex.Apply(false, true);
                    _whiteMask = tex;
                }

                return _whiteMask;
            }
        }

        public static Texture2D GetRoundedFillMask(int radius)
        {
            int r = Mathf.Clamp(radius, 1, 64);
            Texture2D tex;
            if (!FillMasks.TryGetValue(r, out tex) || tex == null)
            {
                tex = CreateRoundedSdfMask(r, 0f);
                FillMasks[r] = tex;
            }

            return tex;
        }

        public static Texture2D GetRoundedStrokeMask(int radius, float strokeWidth)
        {
            int r = Mathf.Clamp(radius, 1, 64);
            int wQuant = Mathf.Clamp(Mathf.RoundToInt(strokeWidth * 10f), 5, 50);
            long key = ((long)r << 16) | (uint)wQuant;
            Texture2D tex;
            if (!StrokeMasks.TryGetValue(key, out tex) || tex == null)
            {
                tex = CreateRoundedSdfMask(r, wQuant / 10f);
                StrokeMasks[key] = tex;
            }

            return tex;
        }

        public static Texture2D GetShadowMask(int radius, int blur)
        {
            int r = Mathf.Clamp(radius, 4, 48);
            int b = Mathf.Clamp(blur, 4, 32);
            long key = ((long)r << 16) | (uint)b;
            Texture2D tex;
            if (!ShadowMasks.TryGetValue(key, out tex) || tex == null)
            {
                tex = CreateShadowSdfTexture(r, b);
                ShadowMasks[key] = tex;
            }

            return tex;
        }

        public static Texture2D GetCircle(int diameter)
        {
            int d = Mathf.Clamp(diameter, 8, 128);
            Texture2D tex;
            if (!CircleMasks.TryGetValue(d, out tex) || tex == null)
            {
                var t = CreateTex(d, d, TextureWrapMode.Clamp);
                var px = new Color32[d * d];
                float c = (d - 1) * 0.5f;
                float r = d * 0.5f - 0.8f;
                for (int y = 0; y < d; y++)
                {
                    for (int x = 0; x < d; x++)
                    {
                        float dist = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) - r;
                        float a = Mathf.Clamp01(0.5f - dist);
                        px[y * d + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                    }
                }

                t.SetPixels32(px);
                t.Apply(false, true);
                CircleMasks[d] = t;
                tex = t;
            }

            return tex;
        }

        public static Sprite CreateCircleSprite(int diameter)
        {
            Texture2D tex = GetCircle(diameter);
            Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            sp.hideFlags = HideFlags.HideAndDontSave;
            CreatedSprites.Add(sp);
            return sp;
        }

        /// <summary>路径 B 降级用：按量化尺寸生成圆角品牌渐变胶囊纹理（有上限防爆显存）。</summary>
        public static Texture2D GetGradientCapsule(int width, int height, int radius, bool soft)
        {
            int w = Mathf.Clamp(Mathf.RoundToInt(width / 8f) * 8, 16, 800);
            int h = Mathf.Clamp(Mathf.RoundToInt(height / 2f) * 2, 8, 128);
            int r = Mathf.Clamp(radius, 1, Math.Min(w, h) / 2);
            long key = ((long)w << 24) | ((long)h << 12) | ((long)r << 2) | (soft ? 1L : 0L);

            Texture2D cached;
            if (GradientCapsuleCache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            if (GradientCapsuleCache.Count >= 24)
            {
                while (GradientCapsuleOrder.Count > 0)
                {
                    long oldest = GradientCapsuleOrder.Dequeue();
                    Texture2D oldTex;
                    if (GradientCapsuleCache.TryGetValue(oldest, out oldTex))
                    {
                        GradientCapsuleCache.Remove(oldest);
                        SafeDestroy(ref oldTex);
                        break;
                    }
                }
            }

            var tex = CreateTex(w, h, TextureWrapMode.Clamp);
            var px = new Color32[w * h];
            float cx = (w - 1) * 0.5f;
            float cy = (h - 1) * 0.5f;
            float hx = cx - 0.5f;
            float hy = cy - 0.5f;

            for (int y = 0; y < h; y++)
            {
                float qy = Mathf.Abs(y - cy) - (hy - r);
                for (int x = 0; x < w; x++)
                {
                    float qx = Mathf.Abs(x - cx) - (hx - r);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude +
                                    Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                    float a = Mathf.Clamp01(0.5f - outside);
                    float u = x / (float)Mathf.Max(1, w - 1);
                    Color col = soft
                        ? Color.Lerp(KugouTheme.WithAlpha(KugouTheme.Colors.AccentSky, 0.24f),
                                     KugouTheme.WithAlpha(KugouTheme.Colors.AccentLavender, 0.24f), u)
                        : SampleBrandGradient(u);
                    col.a *= a;
                    px[y * w + x] = col;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            GradientCapsuleCache[key] = tex;
            GradientCapsuleOrder.Enqueue(key);
            return tex;
        }

        /// <summary>按歌单名称哈希生成优雅的 Lo-Fi 渐变方块底图。</summary>
        public static Texture2D GetMonogramBackdrop(string seedText)
        {
            int bucket = Math.Abs((seedText ?? "kugou").GetHashCode()) % 12;
            Texture2D tex;
            if (MonogramCache.TryGetValue(bucket, out tex) && tex != null)
            {
                return tex;
            }

            float t = bucket / 11f;
            Color cA = Color.Lerp(new Color(0.24f, 0.56f, 0.85f, 1f), new Color(0.55f, 0.36f, 0.86f, 1f), t);
            Color cB = Color.Lerp(new Color(0.49f, 0.38f, 0.88f, 1f), new Color(0.90f, 0.38f, 0.56f, 1f), t);
            const int size = 32;
            tex = CreateTex(size, size, TextureWrapMode.Clamp);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + (size - 1 - y)) / (float)(2 * (size - 1));
                    px[y * size + x] = Color.Lerp(cA, cB, u);
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            MonogramCache[bucket] = tex;
            return tex;
        }

        public static Color SampleBrandGradient(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f
                ? Color.Lerp(KugouTheme.Colors.AccentSky, KugouTheme.Colors.AccentLavender, t * 2f)
                : Color.Lerp(KugouTheme.Colors.AccentLavender, KugouTheme.Colors.AccentCoral, (t - 0.5f) * 2f);
        }

        public static void DisposeAll()
        {
            SafeDestroy(ref _brandGradient);
            SafeDestroy(ref _brandGradientSoft);
            SafeDestroy(ref _topHighlight);
            SafeDestroy(ref _radialGlow);
            SafeDestroy(ref _noiseTile);
            SafeDestroy(ref _featherVeil);
            SafeDestroy(ref _vinylDisc);
            SafeDestroy(ref _scrollThumbMask);
            SafeDestroy(ref _whiteMask);

            DestroyMap(FillMasks);
            DestroyMap(StrokeMasks);
            DestroyMap(ShadowMasks);
            DestroyMap(CircleMasks);
            DestroyMap(GradientCapsuleCache);
            GradientCapsuleOrder.Clear();
            DestroyMap(MonogramCache);

            for (int i = 0; i < CreatedSprites.Count; i++)
            {
                if (CreatedSprites[i] != null)
                {
                    UnityEngine.Object.Destroy(CreatedSprites[i]);
                }
            }

            CreatedSprites.Clear();
        }

        private static Texture2D CreateTex(int width, int height, TextureWrapMode wrap)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            tex.wrapMode = wrap;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static Texture2D CreateHorizontalGradient(int width, Color left, Color mid, Color right)
        {
            var tex = CreateTex(width, 2, TextureWrapMode.Clamp);
            var px = new Color32[width * 2];
            for (int x = 0; x < width; x++)
            {
                float t = x / (float)(width - 1);
                Color c = t < 0.5f ? Color.Lerp(left, mid, t * 2f) : Color.Lerp(mid, right, (t - 0.5f) * 2f);
                Color32 c32 = c;
                px[x] = c32;
                px[width + x] = c32;
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D CreateRoundedSdfMask(int radius, float strokeWidth)
        {
            int s = 2 * radius + 3;
            var tex = CreateTex(s, s, TextureWrapMode.Clamp);
            var px = new Color32[s * s];
            float c = (s - 1) * 0.5f;
            float half = c - 0.5f;

            for (int y = 0; y < s; y++)
            {
                float qy = Mathf.Abs(y - c) - (half - radius);
                for (int x = 0; x < s; x++)
                {
                    float qx = Mathf.Abs(x - c) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude +
                                    Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float a = strokeWidth <= 0f
                        ? Mathf.Clamp01(0.5f - outside)
                        : Mathf.Clamp01(0.5f - (Mathf.Abs(outside + strokeWidth * 0.5f) - strokeWidth * 0.5f));
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D CreateShadowSdfTexture(int radius, int blur)
        {
            int border = radius + blur;
            int s = border * 2 + 3;
            var tex = CreateTex(s, s, TextureWrapMode.Clamp);
            var px = new Color32[s * s];
            float c = (s - 1) * 0.5f;
            float innerHalf = c - blur;

            for (int y = 0; y < s; y++)
            {
                float qy = Mathf.Abs(y - c) - (innerHalf - radius);
                for (int x = 0; x < s; x++)
                {
                    float qx = Mathf.Abs(x - c) - (innerHalf - radius);
                    float dist = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude +
                                 Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float t = SmoothStep(-blur * 0.35f, blur, dist);
                    float a = Mathf.Clamp01(1f - t);
                    a = a * a;
                    px[y * s + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D CreateVinylDiscTexture(int size)
        {
            var tex = CreateTex(size, size, TextureWrapMode.Clamp);
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            float outerR = size * 0.5f - 1f;
            float labelR = size * 0.19f;
            float holeR = size * 0.055f;

            for (int y = 0; y < size; y++)
            {
                float dy = y - c;
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(0.5f - (dist - outerR));

                    if (alpha <= 0f)
                    {
                        px[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    Color col;
                    if (dist < holeR)
                    {
                        col = new Color(0.08f, 0.10f, 0.17f, alpha);
                    }
                    else if (dist < labelR)
                    {
                        float u = Mathf.Clamp01((dx + dy) / (labelR * 2.4f) + 0.5f);
                        col = SampleBrandGradient(u);
                        col.a = alpha;
                    }
                    else
                    {
                        // 黑胶同心微纹 + 轻微高光角区（Lo-Fi 质感）
                        float angle = Mathf.Atan2(dy, dx);
                        float sheen = Mathf.Pow(Mathf.Abs(Mathf.Cos(angle - 0.7f)), 6f) * 0.06f;
                        float groove = (Mathf.Sin(dist * 1.45f) * 0.5f + 0.5f) * 0.03f;
                        float rim = dist > outerR - 1.5f ? 0.14f : 0f;
                        float baseV = 0.08f + sheen + groove + rim;
                        col = new Color(baseV * 0.9f, baseV * 0.95f, baseV * 1.18f, alpha);
                    }

                    px[y * size + x] = col;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static void SafeDestroy(ref Texture2D tex)
        {
            if (tex != null)
            {
                UnityEngine.Object.Destroy(tex);
                tex = null;
            }
        }

        private static void DestroyMap<TKey>(Dictionary<TKey, Texture2D> map)
        {
            foreach (var kv in map)
            {
                if (kv.Value != null)
                {
                    UnityEngine.Object.Destroy(kv.Value);
                }
            }

            map.Clear();
        }
    }
}
