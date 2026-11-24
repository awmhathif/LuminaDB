using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LuminaDB.Core.Web
{
    /// <summary>
    /// A QR code encoder (ISO/IEC 18004, byte mode, versions 1-40) for the demo-mode address. Written here rather than
    /// taken from a package so the app has no extra dependency; the structure follows the reference algorithm
    /// (Reed-Solomon over GF(256), interleaving, eight masks scored by the four penalty rules).
    /// </summary>
    public sealed class QrCode
    {
        public enum Ecc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

        private static readonly int[] EccFormatBits = { 1, 0, 3, 2 };

        private static readonly int[,] EccCodewordsPerBlock =
        {
            { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
            { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        };

        private static readonly int[,] NumErrorCorrectionBlocks =
        {
            { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
            { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
            { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
        };

        private readonly bool[,] _modules;     // [y, x], true = dark
        private readonly bool[,] _isFunction;

        public int Version { get; }
        public int Size { get; }
        public Ecc ErrorCorrection { get; }
        public int Mask { get; private set; }

        public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

        private QrCode(int version, Ecc ecl, byte[] dataCodewords)
        {
            Version = version;
            Size = version * 4 + 17;
            ErrorCorrection = ecl;
            _modules = new bool[Size, Size];
            _isFunction = new bool[Size, Size];
            DrawFunctionPatterns();
            var all = AddEccAndInterleave(dataCodewords);
            DrawCodewords(all);

            int best = 0, minPenalty = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(m);
                DrawFormatBits(m);
                int p = Penalty();
                if (p < minPenalty) { best = m; minPenalty = p; }
                ApplyMask(m);   // XOR again undoes it
            }
            Mask = best;
            ApplyMask(best);
            DrawFormatBits(best);
        }

        /// <summary>Encodes text as UTF-8 bytes in the smallest version that fits; error correction is raised when it costs nothing.</summary>
        public static QrCode EncodeText(string text, Ecc minEcc = Ecc.Medium)
        {
            var data = Encoding.UTF8.GetBytes(text ?? "");
            int version, dataBits = 0;
            for (version = 1; ; version++)
            {
                int capacity = NumDataCodewords(version, minEcc) * 8;
                dataBits = 4 + CharCountBits(version) + data.Length * 8;
                if (dataBits <= capacity) break;
                if (version >= 40) throw new ArgumentException("Text too long for a QR code.");
            }
            var ecl = minEcc;
            foreach (Ecc e in new[] { Ecc.Medium, Ecc.Quartile, Ecc.High })
                if (e > ecl && dataBits <= NumDataCodewords(version, e) * 8) ecl = e;

            var bits = new List<bool>();
            void Append(int val, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
            Append(0x4, 4);                         // byte mode
            Append(data.Length, CharCountBits(version));
            foreach (var b in data) Append(b, 8);

            int capacityBits = NumDataCodewords(version, ecl) * 8;
            Append(0, Math.Min(4, capacityBits - bits.Count));
            Append(0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);

            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return new QrCode(version, ecl, codewords);
        }

        private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

        // ------------------------------------------------------------------ function patterns

        private void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);

            var align = AlignmentPositions();
            int n = align.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                        DrawAlignment(align[i], align[j]);

            DrawFormatBits(0);   // reserves the area; redrawn with the real mask later
            DrawVersion();
        }

        private void DrawFormatBits(int mask)
        {
            int data = EccFormatBits[(int)ErrorCorrection] << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
            SetFunction(8, 7, Bit(bits, 6));
            SetFunction(8, 8, Bit(bits, 7));
            SetFunction(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
            SetFunction(8, Size - 8, true);   // the dark module
        }

        private void DrawVersion()
        {
            if (Version < 7) return;
            int rem = Version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = Version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = Bit(bits, i);
                int a = Size - 11 + i % 3, b = i / 3;
                SetFunction(a, b, bit);
                SetFunction(b, a, bit);
            }
        }

        private void DrawFinder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
                }
        }

        private void DrawAlignment(int x, int y)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        private void SetFunction(int x, int y, bool dark)
        {
            _modules[y, x] = dark;
            _isFunction[y, x] = true;
        }

        private int[] AlignmentPositions()
        {
            if (Version == 1) return new int[0];
            int num = Version / 7 + 2;
            int step = Version == 32 ? 26 : (Version * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
            var result = new int[num];
            result[0] = 6;
            for (int i = num - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        // ------------------------------------------------------------------ codewords

        private static int NumRawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        private static int NumDataCodewords(int ver, Ecc ecl) =>
            NumRawDataModules(ver) / 8 - EccCodewordsPerBlock[(int)ecl, ver] * NumErrorCorrectionBlocks[(int)ecl, ver];

        private byte[] AddEccAndInterleave(byte[] data)
        {
            int numBlocks = NumErrorCorrectionBlocks[(int)ErrorCorrection, Version];
            int blockEccLen = EccCodewordsPerBlock[(int)ErrorCorrection, Version];
            int rawCodewords = NumRawDataModules(Version) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            var blocks = new List<byte[]>();
            var divisor = ReedSolomonDivisor(blockEccLen);
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                var ecc = ReedSolomonRemainder(dat, divisor);
                var block = new byte[shortBlockLen + 1];
                Array.Copy(dat, 0, block, 0, datLen);
                // Short blocks get a dummy byte at datLen so all blocks line up; it is skipped when interleaving.
                Array.Copy(ecc, 0, block, block.Length - blockEccLen, blockEccLen);
                blocks.Add(block);
            }

            var result = new List<byte>(rawCodewords);
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                        result.Add(blocks[j][i]);
            return result.ToArray();
        }

        private void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!_isFunction[y, x] && i < data.Length * 8)
                        {
                            _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
                }
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert && !_isFunction[y, x]) _modules[y, x] = !_modules[y, x];
                }
        }

        // ------------------------------------------------------------------ penalty

        private int Penalty()
        {
            int result = 0;
            // Rule 1: runs of 5+ same-colour modules in a row or column. Rule 3: finder-like 1:1:3:1:1 patterns.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < Size; a++)
                {
                    bool runColor = false;
                    int run = 0;
                    var line = new bool[Size];
                    for (int b = 0; b < Size; b++)
                    {
                        bool c = pass == 0 ? _modules[a, b] : _modules[b, a];
                        line[b] = c;
                        if (b > 0 && c == runColor) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                        else { runColor = c; run = 1; }
                    }
                    result += FinderLike(line) * 40;
                }
            }
            // Rule 2: 2x2 blocks of one colour.
            for (int y = 0; y < Size - 1; y++)
                for (int x = 0; x < Size - 1; x++)
                {
                    bool c = _modules[y, x];
                    if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += 3;
                }
            // Rule 4: balance of dark and light.
            int dark = 0;
            foreach (bool m in _modules) if (m) dark++;
            int total = Size * Size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += Math.Max(0, k) * 10;
            return result;
        }

        /// <summary>Counts 1011101 with four light modules on one side (light outside the symbol counts as light).</summary>
        private static int FinderLike(bool[] line)
        {
            int count = 0;
            bool[] pattern = { true, false, true, true, true, false, true };
            bool At(int i) => i >= 0 && i < line.Length && line[i];
            for (int i = -4; i < line.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < 7 && match; j++) if (At(i + j) != pattern[j]) match = false;
                if (!match) continue;
                bool lightBefore = !At(i - 1) && !At(i - 2) && !At(i - 3) && !At(i - 4);
                bool lightAfter = !At(i + 7) && !At(i + 8) && !At(i + 9) && !At(i + 10);
                if (lightBefore || lightAfter) count++;
            }
            return count;
        }

        // ------------------------------------------------------------------ Reed-Solomon

        private static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        private static byte Multiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

        // ------------------------------------------------------------------ output

        /// <summary>A binary PGM image (white quiet zone of 4 modules) — easy to check with any decoder.</summary>
        public void WritePgm(string path, int scale = 8)
        {
            int border = 4, dim = (Size + border * 2) * scale;
            using (var fs = new FileStream(path, FileMode.Create))
            {
                var head = Encoding.ASCII.GetBytes("P5\n" + dim + " " + dim + "\n255\n");
                fs.Write(head, 0, head.Length);
                var row = new byte[dim];
                for (int py = 0; py < dim; py++)
                {
                    int y = py / scale - border;
                    for (int px = 0; px < dim; px++) row[px] = this[px / scale - border, y] ? (byte)0 : (byte)255;
                    fs.Write(row, 0, row.Length);
                }
            }
        }
    }
}
