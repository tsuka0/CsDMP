using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Formats.Asn1.AsnWriter;


namespace MidiPlayer
{
    public class PackedEvent
    {
        public ulong Delta { get; set; }
        public uint Data { get; set; }
        public int OriginalIndex { get; set; } // 安定ソート用
    }

    public class PackedTempo
    {
        public ulong Delta { get; set; }
        public uint Tempo { get; set; }
        public int OriginalIndex { get; set; }
    }

    public static class StableSortExtensions
    {
        public static void StableSort<T>(this List<T> list, Func<T, ulong> keySelector)
        {
            if (list.Count <= 1) return;

            var indexedItems = list.Select((item, index) => new { Item = item, Index = (ulong)index }).ToList();

            // Radix Sort
            var sorted = indexedItems.RadixSort(
                x => keySelector(x.Item),  
                x => x.Index              
            ).ToList();

            for (int i = 0; i < list.Count; i++)
            {
                list[i] = sorted[i].Item;
            }
        }

        private static IEnumerable<T> RadixSort<T>(
            this IEnumerable<T> items,
            Func<T, ulong> mainKeySelector,
            Func<T, ulong> secondaryKeySelector)
        {
            const int bits = 8;
            const int radix = 1 << bits;
            const int passes = sizeof(ulong) * 8 / bits;

            var sortedByMain = items;
            for (int pass = 0; pass < passes; pass++)
            {
                var buckets = new List<T>[radix];
                for (int i = 0; i < radix; i++)
                    buckets[i] = new List<T>();

                foreach (var item in sortedByMain)
                {
                    ulong key = mainKeySelector(item);
                    int digit = (int)((key >> (pass * bits)) & (radix - 1));
                    buckets[digit].Add(item);
                }

                sortedByMain = buckets.SelectMany(b => b);
            }

            return sortedByMain
                .GroupBy(mainKeySelector)
                .OrderBy(g => g.Key)
                .SelectMany(g => g.OrderBy(secondaryKeySelector));
        }
    }

    static class KDMAPI
    {
        public struct MIDIHDR
        {
            IntPtr dwUser;
            IntPtr lpNext;
            IntPtr reserved;
            IntPtr dwReserved;
        }

        public enum OMSettingMode
        {
            OM_SET = 0x0,
            OM_GET = 0x1
        }

        public enum OMSetting
        {
            OM_CAPFRAMERATE = 0x10000,
            OM_DEBUGMMODE = 0x10001,
            OM_DISABLEFADEOUT = 0x10002,
            OM_DONTMISSNOTES = 0x10003,

            OM_ENABLESFX = 0x10004,
            OM_FULLVELOCITY = 0x10005,
            OM_IGNOREVELOCITYRANGE = 0x10006,
            OM_IGNOREALLEVENTS = 0x10007,
            OM_IGNORESYSEX = 0x10008,
            OM_IGNORESYSRESET = 0x10009,
            OM_LIMITRANGETO88 = 0x10010,
            OM_MT32MODE = 0x10011,
            OM_MONORENDERING = 0x10012,
            OM_NOTEOFF1 = 0x10013,
            OM_EVENTPROCWITHAUDIO = 0x10014,
            OM_SINCINTER = 0x10015,
            OM_SLEEPSTATES = 0x10016,

            OM_AUDIOBITDEPTH = 0x10017,
            OM_AUDIOFREQ = 0x10018,
            OM_CURRENTENGINE = 0x10019,
            OM_BUFFERLENGTH = 0x10020,
            OM_MAXRENDERINGTIME = 0x10021,
            OM_MINIGNOREVELRANGE = 0x10022,
            OM_MAXIGNOREVELRANGE = 0x10023,
            OM_OUTPUTVOLUME = 0x10024,
            OM_TRANSPOSE = 0x10025,
            OM_MAXVOICES = 0x10026,
            OM_SINCINTERCONV = 0x10027,

            OM_OVERRIDENOTELENGTH = 0x10028,
            OM_NOTELENGTH = 0x10029,
            OM_ENABLEDELAYNOTEOFF = 0x10030,
            OM_DELAYNOTEOFFVAL = 0x10031
        }

        public struct DebugInfo
        {
            float RenderingTime;
            int[] ActiveVoices;

            double ASIOInputLatency;
            double ASIOOutputLatency;
        }

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern bool ReturnKDMAPIVer(out Int32 Major, out Int32 Minor, out Int32 Build, out Int32 Revision);

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern bool IsKDMAPIAvailable();

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern int InitializeKDMAPIStream();

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern int TerminateKDMAPIStream();

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern void ResetKDMAPIStream();

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern uint SendDirectData(uint dwMsg);

        [DllImport("OmniMIDI\\OmniMIDI")]
        public static extern uint SendDirectDataNoBuf(uint dwMsg);
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            string midiFilePath = ShowFileDialog();

            if (!string.IsNullOrEmpty(midiFilePath))
            {
                using (var game = new Player(midiFilePath))
                {
                    game.Run();
                }
            }
        }

        private static string ShowFileDialog()
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Title = "MIDIファイルを選択";
                openFileDialog.Filter = "MIDIファイル (*.mid)|*.mid|すべてのファイル (*.*)|*.*";
                openFileDialog.RestoreDirectory = true;

                return openFileDialog.ShowDialog() == DialogResult.OK
                    ? openFileDialog.FileName
                    : null;
            }
        }
    }

    public struct TrackColor
    {
        public byte R;
        public byte G;
        public byte B;

        public TrackColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    public struct Rectangle
    {
        public int X;
        public int Y;
        public int W;
        public int H;

        public Rectangle(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }

    public class Player : GameWindow
    {
        private const int WindowWidth = 512;
        private const int WindowHeight = 288;
        private const double PPQNBuffer = 196.0;

        private List<PackedEvent> allEvents = new List<PackedEvent>();
        private List<PackedTempo> tempoChanges = new List<PackedTempo>();
        private sbyte[] visualMemory;
        private TrackColor[] trackColors = new TrackColor[128];
        private ulong maxDelta;
        private ushort ppqn;
        private double ppqnBuffer;

        private bool playing = false;
        private double playCount = 0;
        private int tempoIndex = 0;
        private int eventIndex = 0;
        private DateTime lastTime;
        private double holdTime = 0;
        private double noteSpeed = 1.0;
        private Dictionary<byte, bool> noteStates = new Dictionary<byte, bool>();
        private int lastEventIndex = 0;
        private double fps = 0;
        private int frameCount = 0;
        private DateTime fpsTime;
        private int activeNotes = 0;
        private int playedNotes = 0;
        private int totalNotes = 0;
        private bool showInfo = true;
        private int windowWidth = WindowWidth;
        private int windowHeight = WindowHeight;


        private int program;
        private int vao, vbo, ebo;
        private int texture;

        private byte[] frontBuffer;
        private byte[] backBuffer;
        private bool bufferDirty = true;
        private List<Rectangle> dirtyRegions = new List<Rectangle>();

        private byte[] pat = new byte[128];

        private bool mousePressed = false;
        private double mouseX = 0;
        private bool spacePressed = false;
        private bool kPressed = false;

        private Dictionary<string, byte[]> numberCache = new Dictionary<string, byte[]>();

        private object lockObj = new object();

        // KDMAPI functions
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private delegate void InitializeKDMAPIStreamFunc();
        private delegate int IsKDMAPIAvailableFunc();
        private delegate void ResetKDMAPIStreamFunc();
        private delegate void SendDirectDataFunc(uint data);
        private delegate void TerminateKDMAPIStreamFunc();

        private static IntPtr kdmapiDll = IntPtr.Zero;
        private static InitializeKDMAPIStreamFunc InitializeKDMAPIStream;
        private static IsKDMAPIAvailableFunc IsKDMAPIAvailable;
        private static ResetKDMAPIStreamFunc ResetKDMAPIStream;
        private static SendDirectDataFunc SendDirectData;
        private static TerminateKDMAPIStreamFunc TerminateKDMAPIStream;

        private string vertexShaderSource = @"
            #version 330 core
            layout (location = 0) in vec3 aPos;
            layout (location = 1) in vec2 aTexCoord;
            out vec2 TexCoord;
            void main() {
                gl_Position = vec4(aPos, 1.0);
                TexCoord = aTexCoord;
            }";

                private string fragmentShaderSource = @"
            #version 330 core
            in vec2 TexCoord;
            out vec4 FragColor;
            uniform sampler2D ourTexture;
            void main() {
                FragColor = texture(ourTexture, TexCoord);
            }";

        public Player(string filename) : base(
        new GameWindowSettings()
        {
            UpdateFrequency = 165.0
        },
        new NativeWindowSettings()
        {
            ClientSize = new Vector2i(WindowWidth, WindowHeight),
            Title = "CsDMP",
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible
        })
        {
            var start = DateTime.Now;
            if (!LoadMIDI(filename))
            {
                Console.WriteLine("Failed to load MIDI");
                Environment.Exit(1);
            }
            Console.WriteLine($"MIDI loaded in {DateTime.Now - start}");
            KDMAPI.InitializeKDMAPIStream();
            if (!KDMAPI.IsKDMAPIAvailable())
            {
                Console.WriteLine("KDMAPI is None");
                return;
            }
            InitPianoPattern();
            lastTime = DateTime.Now;
            fpsTime = DateTime.Now;
        }

        private bool LoadMIDI(string filename)
        {
            try
            {
                using (var mmf = MemoryMappedFile.CreateFromFile(filename, FileMode.Open))
                {
                    using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                    {
                        return ParseMIDI(accessor);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading MIDI: {ex.Message}");
                return false;
            }
        }


        private bool ParseMIDI(MemoryMappedViewAccessor accessor)
        {
            byte[] header = new byte[14];
            accessor.ReadArray(0, header, 0, 14);

            if (System.Text.Encoding.ASCII.GetString(header, 0, 4) != "MThd")
            {
                Console.WriteLine("Invalid MIDI file");
                return false;
            }

            ushort format = (ushort)((header[8] << 8) | header[9]);
            ushort tracks = (ushort)((header[10] << 8) | header[11]);
            ppqn = (ushort)((header[12] << 8) | header[13]);

            if (format == 0)
            {
                Console.WriteLine("Format 0 not supported");
                return false;
            }

            ppqnBuffer = PPQNBuffer / ppqn;

            Console.WriteLine($"Format: {format}, Tracks: {tracks}, PPQN: {ppqn}");

            var result = TrackParse(accessor, 14, tracks, ppqnBuffer);


            allEvents = result.events;
            tempoChanges = result.tempos;
            visualMemory = result.visualMemory;
            maxDelta = result.maxDelta;

            totalNotes = allEvents.Count(e => ((e.Data >> 4) & 0xF) == 0x9 && ((e.Data >> 16) & 0xFF) > 0);

            GenerateTrackColors();
            fpsTime = DateTime.Now;

            Console.WriteLine($"Loaded {allEvents.Count} events, {tempoChanges.Count} tempo changes, {totalNotes} total notes");

            return true;
        }

        public class ParseResult
        {
            public List<PackedEvent> events { get; set; }
            public List<PackedTempo> tempos { get; set; }
            public sbyte[] visualMemory { get; set; }
            public ulong maxDelta { get; set; }
        }



        public unsafe static ParseResult TrackParse(MemoryMappedViewAccessor accessor, long startPos, int numTracks, double ppqnBuffer)
        {
            int maxNoteLength = 15;

            int visualSize = 300_000_000;
            sbyte[] visualMemory = new sbyte[visualSize];

            int noteSize = 500_000_000;
            var noteData = new List<uint>(noteSize);
            var noteDelta = new List<ulong>(noteSize);

            int eventSize = 10_000_000;
            var eventData = new List<uint>(eventSize);
            var eventDelta = new List<ulong>(eventSize);

            List<double[]> strangeTempo = new List<double[]>(1000);

            int tempoCount = 0;
            ulong maxDelta = 0;

            uint[] putKey = new uint[128 * 16];
            uint[] putDelta = new uint[128 * 16];
            uint[] vDelta = new uint[128 * 16];
            uint[] check = new uint[128 * 16];

            long fileLength = accessor.Capacity;

            byte* basePtr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

            byte* ptr = basePtr + startPos;
            try
            {
                for (int trackNum = 0; trackNum < numTracks; trackNum++)
                {
                    if (ptr + 8 > basePtr + fileLength) break;

                    if (ptr[0] != 'M' || ptr[1] != 'T' || ptr[2] != 'r' || ptr[3] != 'k') break;

                    uint length = ((uint)ptr[4] << 24) | ((uint)ptr[5] << 16) | ((uint)ptr[6] << 8) | ptr[7];
                    byte* trackEnd = ptr + 8 + length;
                    ptr += 8;

                    ulong delta = 0;
                    byte runningStatus = 0;

                    while (ptr < trackEnd)
                    {
                        if (strangeTempo.Count <= tempoCount)
                            strangeTempo.Add(new double[2]);


                        ulong deltaInc = ReadVariableLengthUnsafe(ref ptr);
                        delta += deltaInc;

                        byte statusByte = *ptr++;
                        if (statusByte == 0xFF)
                        {
                            byte metaType = *ptr++;
                            ulong metaLength = ReadVariableLengthUnsafe(ref ptr);

                            if (metaType == 0x51 && metaLength == 3)
                            {
                                uint value = (uint)((ptr[0] << 16) | (ptr[1] << 8) | ptr[2]);
                                double tempoValue = 60000000.0 / value;
                                strangeTempo[tempoCount][0] = delta;
                                strangeTempo[tempoCount][1] = tempoValue;
                                tempoCount++;
                            }
                            ptr += (long)metaLength;
                            runningStatus = 0;
                            continue;
                        }

                        if (statusByte == 0xF0 || statusByte == 0xF7)
                        {
                            ulong sysexLength = ReadVariableLengthUnsafe(ref ptr);
                            ptr += (long)sysexLength;
                            runningStatus = 0;
                            continue;
                        }

                        if (statusByte < 0x80)
                        {
                            statusByte = runningStatus;
                            ptr--;
                        }
                        else
                        {
                            runningStatus = statusByte;
                        }

                        byte statusType = (byte)(statusByte >> 4);
                        byte channel = (byte)(statusByte & 0x0F);

                        if (statusType == 0x8 || (statusType == 0x9 && ptr[1] == 0))
                        {
                            byte note = *ptr;
                            int vCount = channel * 128 + note;

                            if (putKey[vCount] > 0)
                            {
                                putKey[vCount]--;
                                noteData.Add((0u << 16) | ((uint)note << 8) | statusByte);
                                noteDelta.Add(delta);
                            }

                            if (vDelta[vCount] > delta || check[vCount] == 0)
                            {
                                ptr += 2;
                                continue;
                            }
                            else
                            {
                                sbyte chColor = (sbyte)(channel + 1);
                                int start = (int)(vDelta[vCount] * ppqnBuffer) * 128 + note;
                                if (start < visualMemory.Length)
                                    visualMemory[start] = (sbyte)(-chColor);

                                int endNote = (int)(delta * ppqnBuffer) * 128 + note;
                                start += 128;
                                bool vFrag = false;

                                for (int goIdx = start; goIdx < endNote && goIdx < visualMemory.Length; goIdx += 128)
                                {
                                    if (vFrag)
                                    {
                                        if (visualMemory[goIdx] == 0)
                                        {
                                            visualMemory[goIdx] = chColor;
                                            vFrag = false;
                                        }
                                    }
                                    else
                                    {
                                        if (visualMemory[goIdx] < 0)
                                        {
                                            vFrag = true;
                                            continue;
                                        }
                                        else
                                        {
                                            visualMemory[goIdx] = chColor;
                                        }
                                    }
                                }
                            }
                            ptr += 2;
                        }
                        else if (statusType == 0x9)
                        {
                            byte note = *ptr;
                            byte velocity = ptr[1];
                            int vCount = channel * 128 + note;
                            vDelta[vCount] = (uint)delta;

                            if (putDelta[vCount] > delta)
                                putDelta[vCount] = 0;
                            if (check[vCount] == 0)
                                check[vCount] = 1;

                            if (velocity >= 8)
                            {
                                if ((long)delta - (long)putDelta[vCount] > maxNoteLength || putDelta[vCount] == 0)
                                {
                                    putKey[vCount]++;
                                    putDelta[vCount] = (uint)delta;
                                    noteData.Add(((uint)velocity << 16) | ((uint)note << 8) | statusByte);
                                    noteDelta.Add(delta);
                                }
                            }
                            ptr += 2;
                        }
                        else if (statusType >= 0xA && statusType <= 0xE)
                        {
                            int eventLength = (statusType != 0xC && statusType != 0xD) ? 2 : 1;

                            uint eventValue;
                            if (eventLength == 1)
                                eventValue = ((uint)(*ptr) << 8) | statusByte;
                            else
                                eventValue = ((uint)(ptr[1]) << 16) | ((uint)(*ptr) << 8) | statusByte;

                            eventData.Add(eventValue);
                            eventDelta.Add(delta);
                            ptr += eventLength;
                        }
                    }

                    if (maxDelta < delta)
                        maxDelta = delta;
                }

                var events = new List<PackedEvent>(eventData.Count + noteData.Count);
                for (int i = 0; i < eventData.Count; i++)
                    events.Add(new PackedEvent { Delta = eventDelta[i], Data = eventData[i], OriginalIndex = i });
                for (int i = 0; i < noteData.Count; i++)
                    events.Add(new PackedEvent { Delta = noteDelta[i], Data = noteData[i], OriginalIndex = eventData.Count + i });

                events.StableSort(x => x.Delta);

                var tempos = new List<PackedTempo>(tempoCount);
                for (int i = 0; i < tempoCount; i++)
                    tempos.Add(new PackedTempo { Delta = (ulong)strangeTempo[i][0], Tempo = (uint)(strangeTempo[i][1] * 1000), OriginalIndex = i });
                tempos.StableSort(x => x.Delta);

                for (int i = 0; i < visualMemory.Length; i++)
                    visualMemory[i] = Math.Abs(visualMemory[i]);

                return new ParseResult
                {
                    events = events,
                    tempos = tempos,
                    visualMemory = visualMemory,
                    maxDelta = maxDelta
                };
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }


        unsafe private static ulong ReadVariableLengthUnsafe(ref byte* ptr)
        {
            ulong value = 0;
            byte b;
            do
            {
                b = *ptr++;
                value = (value << 7) | (uint)(b & 0x7F);
            } while ((b & 0x80) != 0);
            return value;
        }

        private void GenerateTrackColors()
        {
            for (int i = 0; i < 128; i++)
            {
                double hue = i / 128.0;
                double saturation = 1.0;
                double value = 1.0;

                var (r, g, b) = HsvToRgb(hue, saturation, value);
                trackColors[i] = new TrackColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
            }
        }

        private (double, double, double) HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h * 6) % 2 - 1));
            double m = v - c;

            double r = 0, g = 0, b = 0;
            switch ((int)(h * 6))
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                case 5: r = c; g = 0; b = x; break;
            }

            return (r + m, g + m, b + m);
        }

        private void InitPianoPattern()
        {
            byte[] pattern = { 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0 };
            for (int i = 0; i < 128; i++)
            {
                pat[i] = pattern[i % 12];
            }
        }

        protected override void OnLoad()
        {
            base.OnLoad();

            // Initialize OpenGL
            GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);

            // Compile shaders
            int vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, vertexShaderSource);
            GL.CompileShader(vertexShader);

            int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, fragmentShaderSource);
            GL.CompileShader(fragmentShader);

            program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);

            GL.DetachShader(program, vertexShader);
            GL.DetachShader(program, fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            // Set up vertex data
            float[] vertices = {
                -1.0f, -1.0f, 0.0f, 0.0f, 1.0f,
                 1.0f, -1.0f, 0.0f, 1.0f, 1.0f,
                 1.0f,  1.0f, 0.0f, 1.0f, 0.0f,
                -1.0f,  1.0f, 0.0f, 0.0f, 0.0f
            };

            uint[] indices = { 0, 1, 2, 2, 3, 0 };

            vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();
            ebo = GL.GenBuffer();

            GL.BindVertexArray(vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));

            // Set up texture
            texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            int bufferSize = WindowHeight * WindowWidth * 3;
            frontBuffer = new byte[bufferSize];
            backBuffer = new byte[bufferSize];
            noteSpeed = 1.0;
            bufferDirty = true;

            // Set callbacks
            MouseDown += (args) =>
            {
                if (args.Button == MouseButton.Left)
                {
                    lock (lockObj)
                    {
                        mousePressed = true;
                        bufferDirty = true;
                    }
                }
            };

            MouseUp += (args) =>
            {
                if (args.Button == MouseButton.Left)
                {
                    lock (lockObj)
                    {
                        mousePressed = false;
                    }
                }
            };

            MouseMove += (args) =>
            {
                lock (lockObj)
                {
                    int ScreenWidth = 512;
                    int ScreenHeight = 288;
                    float targetAspect = (float)ScreenWidth / ScreenHeight;
                    float windowAspect = (float)Size.X / Size.Y;

                    float viewWidth, viewHeight;
                    float viewX, viewY;

                    if (windowAspect > targetAspect)
                    {
                        viewHeight = Size.Y;
                        viewWidth = viewHeight * targetAspect;
                        viewX = (Size.X - viewWidth) / 2;
                        viewY = 0;
                    }
                    else
                    {
                        viewWidth = Size.X;
                        viewHeight = viewWidth / targetAspect;
                        viewX = 0;
                        viewY = (Size.Y - viewHeight) / 2;
                    }

                    if (args.Position.X >= viewX && args.Position.X <= viewX + viewWidth &&
                        args.Position.Y >= viewY && args.Position.Y <= viewY + viewHeight)
                    {
                        float relativeX = (args.Position.X - viewX) / viewWidth * ScreenWidth;
                        if (Math.Abs(mouseX - relativeX) > 1.0)
                        {
                            mouseX = relativeX;
                            if (mousePressed)
                            {
                                bufferDirty = true;
                            }
                        }
                    }
                }
            };

            KeyDown += (args) =>
            {
                lock (lockObj)
                {
                    if (args.Key == OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space && !spacePressed)
                    {
                        playing = !playing;
                        holdTime = playCount;
                        spacePressed = true;
                        bufferDirty = true;
                    }
                    else if (args.Key == OpenTK.Windowing.GraphicsLibraryFramework.Keys.K && !kPressed)
                    {
                        showInfo = !showInfo;
                        kPressed = true;
                        bufferDirty = true;
                        Console.WriteLine($"Info display: {showInfo}");
                    }
                    else if (args.Key == OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape)
                    {
                        Close();
                    }
                }
            };

            KeyUp += (args) =>
            {
                lock (lockObj)
                {
                    if (args.Key == OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space)
                    {
                        spacePressed = false;
                    }
                    else if (args.Key == OpenTK.Windowing.GraphicsLibraryFramework.Keys.K)
                    {
                        kPressed = false;
                    }
                }
            };

            Resize += (args) =>
            {
                lock (lockObj)
                {
                    windowWidth = Size.X;
                    windowHeight = Size.Y;
                    bufferDirty = true;
                }
            };
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);

            lock (lockObj)
            {
                frameCount++;
                if ((DateTime.Now - fpsTime).TotalSeconds >= 1.0)
                {
                    fps = frameCount / (DateTime.Now - fpsTime).TotalSeconds;
                    frameCount = 0;
                    fpsTime = DateTime.Now;
                }

                if (mousePressed)
                {
                    double pointerRatio = Math.Max(0, Math.Min((mouseX - 12) / 488, 1));
                    holdTime = pointerRatio * maxDelta;
                    playCount = holdTime;
                    lastTime = DateTime.Now;

                    tempoIndex = 0;
                    for (int i = 0; i < tempoChanges.Count; i++)
                    {
                        if ((double)tempoChanges[i].Delta <= playCount)
                        {
                            tempoIndex = i;
                        }
                        else
                        {
                            break;
                        }
                    }

                    eventIndex = 0;
                    for (int i = 0; i < allEvents.Count; i++)
                    {
                        if ((double)allEvents[i].Delta < playCount)
                        {
                            eventIndex = i + 1;
                        }
                        else
                        {
                            break;
                        }
                    }

                    ResetNoteCounts();
                    KDMAPI.ResetKDMAPIStream();
                    bufferDirty = true;
                }
                else if (playing)
                {
                    DateTime now = DateTime.Now;
                    double elapsed = (now - lastTime).TotalSeconds;
                    double currentTempo = tempoChanges[tempoIndex].Tempo / 1000.0;

                    playCount = elapsed * currentTempo / 60.0 * ppqn + holdTime;

                    while (tempoIndex < tempoChanges.Count - 1 && playCount > tempoChanges[tempoIndex + 1].Delta)
                    {
                        var nextTempo = tempoChanges[tempoIndex + 1];
                        holdTime = nextTempo.Delta + (playCount - nextTempo.Delta) / currentTempo * nextTempo.Tempo / 1000.0;
                        lastTime = DateTime.Now;
                        playCount = holdTime;
                        tempoIndex++;
                        currentTempo = nextTempo.Tempo / 1000.0;
                    }

                    const int MAX_1F_NOTES = 2000;
                    const int SEARCH_STEP = 100000;

                    unsafe
                    {
                        List<uint> eventsToSend = new();

                        while (eventIndex < allEvents.Count && (double)allEvents[eventIndex].Delta < playCount)
                        {
                            eventsToSend.Add(allEvents[eventIndex].Data);
                            eventIndex++;

                            if (eventsToSend.Count > MAX_1F_NOTES)
                            {
                                while (eventIndex < allEvents.Count && (double)allEvents[eventIndex].Delta < playCount)
                                {
                                    eventIndex += SEARCH_STEP;
                                }

                                if (eventIndex >= allEvents.Count)
                                    eventIndex = allEvents.Count - 1;

                                int left = eventIndex - SEARCH_STEP;
                                int right = eventIndex;

                                while (left < right)
                                {
                                    int mid = left + ((right - left) >> 1);
                                    if ((double)allEvents[mid].Delta < playCount)
                                        left = mid + 1;
                                    else
                                        right = mid;
                                }

                                eventIndex = left;

                                break;
                            }
                        }

                        foreach (var evt in eventsToSend)
                        {
                            KDMAPI.SendDirectDataNoBuf(evt);
                        }
                    }

                    bufferDirty = true;
                }
                else
                {
                    lastTime = DateTime.Now;
                    KDMAPI.ResetKDMAPIStream();
                }
            }
        }

        private void ResetNoteCounts()
        {
            noteStates.Clear();
            activeNotes = 0;
            playedNotes = 0;
            lastEventIndex = 0;

            for (int i = 0; i < eventIndex; i++)
            {
                var eventData = allEvents[i];
                byte statusType = (byte)((eventData.Data >> 4) & 0xF);
                byte note = (byte)((eventData.Data >> 8) & 0xFF);

                if (statusType == 0x9)
                {
                    byte velocity = (byte)((eventData.Data >> 16) & 0xFF);
                    if (velocity > 0)
                    {
                        if (!noteStates.ContainsKey(note) || !noteStates[note])
                        {
                            activeNotes++;
                        }
                        noteStates[note] = true;
                        playedNotes++;
                    }
                }
                else if (statusType == 0x8 || (statusType == 0x9 && ((eventData.Data >> 16) & 0xFF) == 0))
                {
                    if (noteStates.ContainsKey(note) && noteStates[note])
                    {
                        activeNotes--;
                    }
                    noteStates[note] = false;
                }
            }
            lastEventIndex = eventIndex;
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);

            lock (lockObj)
            {
                int virtualWidth = 512;
                int virtualHeight = 288;

                float targetAspect = (float)virtualWidth / virtualHeight;
                float windowAspect = (float)windowWidth / windowHeight;

                int viewWidth, viewHeight;
                int viewX, viewY;

                viewHeight = windowHeight;
                viewWidth = (int)(viewHeight * targetAspect);

                if (viewWidth > windowWidth)
                {
                    viewWidth = windowWidth;
                    viewHeight = (int)(viewWidth / targetAspect);
                }

                viewX = (windowWidth - viewWidth) / 2;
                viewY = (windowHeight - viewHeight) / 2;

                if (bufferDirty)
                {
                    ClearBuffer();

                    int pointerX = (int)Math.Floor((playCount / maxDelta) * 488) + 12;
                    int loopStart = (int)Math.Floor(playCount * ppqnBuffer) * 128;

                    RenderFrame(loopStart);
                    RenderUI(pointerX);

                    if (!playing)
                    {
                        DrawStopMark(230, 130, 8, 32, 8, new TrackColor(255, 0, 0)); // 赤いSTOPマーク
                    }

                    if (showInfo)
                    {
                        DrawInfoPanel();
                    }

                    var temp = frontBuffer;
                    frontBuffer = backBuffer;
                    backBuffer = temp;
                    bufferDirty = false;
                }

                GL.Viewport(0, 0, windowWidth, windowHeight);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                GL.Viewport(viewX, viewY, viewWidth, viewHeight);

                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb,
                    virtualWidth, virtualHeight, 0,
                    PixelFormat.Rgb, PixelType.UnsignedByte, frontBuffer);

                GL.UseProgram(program);
                GL.BindVertexArray(vao);
                GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
            }

            SwapBuffers();
        }

        private void ClearBuffer()
        {
            Array.Clear(backBuffer, 0, backBuffer.Length);
        }

        private new void RenderFrame(int loopGo)
        {
            for (int columnIndex = 0; columnIndex < 128; columnIndex++)
            {
                int currentPos = loopGo + columnIndex;
                int memoryStart = -1;
                int memoryStartY = -1;
                double yPos = 250.0;
                int x = columnIndex * 4 + 2;

                for (int a = 0; a < (int)(230 / noteSpeed); a++)
                {
                    if (currentPos >= visualMemory.Length)
                    {
                        break;
                    }

                    short value = visualMemory[currentPos];

                    if (memoryStart == -1 && value > 0)
                    {
                        memoryStart = currentPos;
                        memoryStartY = (int)yPos;
                    }
                    else if (memoryStart != -1 &&
                            (currentPos >= visualMemory.Length ||
                             value != visualMemory[memoryStart]))
                    {
                        int colorIdx = (visualMemory[memoryStart] - 1) * 8;
                        if (colorIdx >= 0 && colorIdx < 128)
                        {
                            var color = trackColors[colorIdx];
                            int y1 = (int)yPos;
                            int y2 = memoryStartY;
                            if (y1 > y2)
                            {
                                (y1, y2) = (y2, y1);
                            }

                            FillRect(x - 2, y1, 4, y2 - y1, color);
                        }

                        if (currentPos >= visualMemory.Length || value == 0)
                        {
                            memoryStart = -1;
                            memoryStartY = -1;
                        }
                        else
                        {
                            memoryStart = currentPos;
                            memoryStartY = (int)yPos;
                        }
                    }

                    currentPos += 128;
                    yPos -= noteSpeed;
                }

                if (memoryStart != -1 && memoryStart < visualMemory.Length)
                {
                    int colorIdx = (visualMemory[memoryStart] - 1) * 8;
                    if (colorIdx >= 0 && colorIdx < 128)
                    {
                        var color = trackColors[colorIdx];
                        int y1 = 20;
                        int y2 = memoryStartY;
                        if (y1 > y2)
                        {
                            (y1, y2) = (y2, y1);
                        }

                        FillRect(x - 2, y1, 4, y2 - y1, color);
                    }
                }
            }

            int loopPosition = loopGo - 1;
            for (int mode = 0; mode < 2; mode++)
            {
                for (int noteIndex = 0; noteIndex < 128; noteIndex++)
                {
                    loopPosition++;
                    if (pat[noteIndex] != mode)
                    {
                        continue;
                    }

                    short value = loopPosition < visualMemory.Length ? visualMemory[loopPosition] : (short)0;
                    var color = value == 0 ?
                        new TrackColor(255, 255, 255) :
                        GetTrackColor(value);

                    double colorScale = mode == 1 ? 0.5 : 0.8;
                    var scaledColor = ScaleColor(color, colorScale);

                    int xPos = noteIndex * 4 + 1;
                    var (x1, x2, y1, y2) = GetNoteRect(mode, xPos);

                    FillRect(x1, y1, x2 - x1, y2 - y1, scaledColor);
                }
            }
        }

        private TrackColor GetTrackColor(short value)
        {
            int colorIdx = (value - 1) * 8;
            return colorIdx >= 0 && colorIdx < 128 ?
                trackColors[colorIdx] :
                new TrackColor(255, 255, 255);
        }

        private TrackColor ScaleColor(TrackColor color, double scale)
        {
            return new TrackColor(
                (byte)(color.R * scale),
                (byte)(color.G * scale),
                (byte)(color.B * scale));
        }

        private (int x1, int x2, int y1, int y2) GetNoteRect(int mode, int x)
        {
            return mode == 0 ?
                ((int)Math.Max(0, x - 4), (int)Math.Min(512, x + 4), 250, 288) :
                ((int)Math.Max(0, x - 1), (int)Math.Min(512, x + 2), 250, 272);
        }

        private void FillRect(int x, int y, int w, int h, TrackColor color)
        {
            if (x < 0 || y < 0 || x + w > WindowWidth || y + h > WindowHeight)
            {
                return;
            }

            for (int row = 0; row < h; row++)
            {
                int startIdx = ((y + row) * WindowWidth + x) * 3;
                for (int col = 0; col < w; col++)
                {
                    int idx = startIdx + col * 3;
                    backBuffer[idx] = color.R;
                    backBuffer[idx + 1] = color.G;
                    backBuffer[idx + 2] = color.B;
                }
            }
        }

        private void RenderUI(int pointerX)
        {
            FillRect(0, 248, 512, 3, new TrackColor(255, 0, 0));

            FillRect(0, 11, 512, 11, new TrackColor(0, 0, 0));

        }

        private void DrawInfoPanel()
        {
            DrawFloat(fps, 10, 30, new TrackColor(255, 255, 0));
        }

        private void DrawDigit(int digit, int x, int y, TrackColor color)
        {
            byte[][] patterns = new byte[][]
            {
                new byte[] {0x3C, 0x66, 0x6E, 0x76, 0x66, 0x66, 0x3C, 0x00},
                new byte[] {0x18, 0x18, 0x38, 0x18, 0x18, 0x18, 0x7E, 0x00},
                new byte[] {0x3C, 0x66, 0x06, 0x0C, 0x30, 0x60, 0x7E, 0x00},
                new byte[] {0x3C, 0x66, 0x06, 0x1C, 0x06, 0x66, 0x3C, 0x00},
                new byte[] {0x06, 0x0E, 0x1E, 0x66, 0x7F, 0x06, 0x06, 0x00},
                new byte[] {0x7E, 0x60, 0x7C, 0x06, 0x06, 0x66, 0x3C, 0x00},
                new byte[] {0x3C, 0x66, 0x60, 0x7C, 0x66, 0x66, 0x3C, 0x00},
                new byte[] {0x7E, 0x66, 0x0C, 0x18, 0x18, 0x18, 0x18, 0x00},
                new byte[] {0x3C, 0x66, 0x66, 0x3C, 0x66, 0x66, 0x3C, 0x00},
                new byte[] {0x3C, 0x66, 0x66, 0x3E, 0x06, 0x66, 0x3C, 0x00}
            };

            if (digit < 0 || digit > 9)
            {
                return;
            }

            byte[] pattern = patterns[digit];
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    if ((pattern[row] & (0x80 >> col)) != 0)
                    {
                        int px = x + col;
                        int py = y + row;
                        if (px >= 0 && px < WindowWidth && py >= 0 && py < WindowHeight)
                        {
                            int idx = (py * WindowWidth + px) * 3;
                            backBuffer[idx] = color.R;
                            backBuffer[idx + 1] = color.G;
                            backBuffer[idx + 2] = color.B;
                        }
                    }
                }
            }
        }

        private void DrawFloat(double num, int x, int y, TrackColor color)
        {
            string numStr = num.ToString("0.0");
            for (int i = 0; i < numStr.Length; i++)
            {
                char c = numStr[i];
                if (c == '.')
                {
                    int px = x + i * 8 + 3;
                    int py = y + 6;
                    if (px >= 0 && px < WindowWidth && py >= 0 && py < WindowHeight)
                    {
                        int idx = (py * WindowWidth + px) * 3;
                        backBuffer[idx] = color.R;
                        backBuffer[idx + 1] = color.G;
                        backBuffer[idx + 2] = color.B;
                    }
                }
                else if (char.IsDigit(c))
                {
                    DrawDigit(c - '0', x + i * 8, y, color);
                }
            }
        }

        private void DrawRect(int x, int y, int width, int height, TrackColor color)
        {
            for (int j = 0; j < height; j++)
            {
                int py = y + j;
                if (py < 0 || py >= WindowHeight) continue;

                for (int i = 0; i < width; i++)
                {
                    int px = x + i;
                    if (px < 0 || px >= WindowWidth) continue;

                    int idx = (py * WindowWidth + px) * 3;
                    backBuffer[idx] = color.R;
                    backBuffer[idx + 1] = color.G;
                    backBuffer[idx + 2] = color.B;
                }
            }
        }
        private void DrawStopMark(int x, int y, int width, int height, int spacing, TrackColor color)
        {
            DrawRect(x, y, width, height, color);

            DrawRect(x + width + spacing, y, width, height, color);
        }


        private void DrawLetter(char c, int x, int y, TrackColor color)
        {
            Dictionary<char, byte[]> letterPatterns = new Dictionary<char, byte[]>
            {
                {'F', new byte[] {0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x60, 0x00}},
                {'P', new byte[] {0x7C, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x00}},
                {'S', new byte[] {0x3C, 0x66, 0x60, 0x3C, 0x06, 0x66, 0x3C, 0x00}}
            };

            if (letterPatterns.TryGetValue(c, out byte[] pattern))
            {
                for (int row = 0; row < 8; row++)
                {
                    for (int col = 0; col < 8; col++)
                    {
                        if ((pattern[row] & (0x80 >> col)) != 0)
                        {
                            int px = x + col;
                            int py = y + row;
                            if (px >= 0 && px < WindowWidth && py >= 0 && py < WindowHeight)
                            {
                                int idx = (py * WindowWidth + px) * 3;
                                backBuffer[idx] = color.R;
                                backBuffer[idx + 1] = color.G;
                                backBuffer[idx + 2] = color.B;
                            }
                        }
                    }
                }
            }
        }
    }
}

