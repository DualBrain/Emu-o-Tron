﻿using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using EmuoTron;
using NetPlay;

namespace DirectXEmu
{
    enum SystemState
    {
        Playing,
        Empty,
        Paused,
        SystemPause
    }
    public struct AutoFire
    {
        public bool on;
        public int freq;
        public int count;
    }
    public partial class Program : Form
    {
        IRenderer renderer;
        IAudio audio;
        IInput input;
        IScaler imageScaler;

        WAVOutput wavRecorder;
        AVSOutput avsRecorder;

        string appPath = "";
        string romPath = "";
        NESCore cpu;
        Controller player1;
        AutoFire player1A;
        AutoFire player1B;
        Controller player1Pressed;
        Controller player2;
        AutoFire player2A;
        AutoFire player2B;
        Controller player2Pressed;
        bool controlStrobe = false;
        uint[] colorChart = new uint[0x200];
        public int frame = 0;
        public int frameSkipper = 1;
        int maxFrameSkip = 10;
        GameGenie[] gameGenieCodes = new GameGenie[0xFF];
        int gameGenieCodeCount = 0;
        string message = "";
        bool storeState;
        int messageDuration = 0;
        bool loadState;
        bool fm2Reset;
        BinaryReader fm2File;
        bool playMovie;
        Keybinds keyBindings;
        SystemState state;
        int generateLine = 0;
        bool generateNameTables = false;
        int nameTableUpdate = 10;
        bool frameAdvance = false;
        bool frameDevance = false;

        int generatePatternLine = 0;
        bool generatePatternTables = false;
        int patternTableUpdate = 3;

        NameTablePreview nameTablePreview;
        PatternTablePreview patternTablePreview;
        MemoryViewer memoryViewer;
        Debugger debugger;
        SoundVis soundVis;
        MemoryVis memoryVis;
        EmuConfig config;

        SoundVolume volume;
        SoundConfig soundConfig;

        int frames = 0;
        int lastTickCount = 0;
        int lastFrameRate = 0;
        private OpenFileDialog openPaletteDialog;
        private OpenFileDialog openMovieDialog;
        bool showFPS;
        bool showInput;


        int quickSaveSlot = 0;
        SaveState[] saveSlots;

        SaveState[] saveBuffer;
        int saveBufferCounter = 0;
        int saveBufferAvaliable = 0;
        int saveBufferFreq;
        int saveBufferSeconds;
        bool saveSafeRewind = false;
        bool rewinding = false;
        bool rewindingEnabled;

        private bool fullScreen = false;
        private Point smallLocation;
        private Size smallSize;

        byte[] movie = new byte[60 * 60 * 60 * 12];//twelve hours should be enough
        int moviePtr = 0;

        bool netPlay;
        bool netPlayServer;
        NetPlayServer netServer;
        NetPlayClient netClient;

        NSFScreen nsfScreen;
        bool Closed;
        bool ResetVideo;

        static bool isMono;

        [STAThread]
        static void Main(string[] args)
        {
            Type t = Type.GetType("Mono.Runtime");
            isMono = t != null;
            if (!isMono)
            {
                Process thisProc = Process.GetCurrentProcess();
                thisProc.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Program prg;
            if (args.Length == 0)
                prg = new Program();
            else
                prg = new Program(args[0]);
            prg.Show();
            prg.Run();
            //SlimDX.Windows.MessagePump.Run(prg, new SlimDX.Windows.MainLoop(prg.Run));
        }

        public Program(string arg = "")
        {
            this.romPath = arg;
            InitializeComponent();
            this.Initialize();
#if !DEBUG
            this.openWithFXCEUToolStripMenuItem.Dispose();
            this.toolStripSeparator3.Dispose();
#endif
        }
        public unsafe void Run()
        {
            while (!Closed) //Crazy ghetto message loop to try and switch away from forced SlimDX
            {
                if (ResetVideo)
                {
                    renderer.Reset();
                    ResetVideo = false;
                }
                if (state == SystemState.Playing && !cpu.debug.debugInterrupt)
                {
                    if(frameAdvance)
                    {
                        frameAdvance = false;
                        state = SystemState.Paused;
                        cpu.APU.SetFPS(cpu.APU.FPS);
                    }
                    input.MainLoop();
                    debugger.smartUpdate = false;
                    RunCPU();
                    if (frameDevance)
                    {
                        frameDevance = false;
                        rewinding = false;
                        state = SystemState.Paused;
                        cpu.APU.SetFPS(cpu.APU.FPS);
                    }
                    if (cpu.debug.pendingError)
                    {
                        cpu.debug.pendingError = false;
                        message = cpu.debug.errorMessage;
                        messageDuration = 90;
                    }
                    if (frameSkipper == 1)//disable audio during turbo
                    {
                        audio.SyncToAudio();
                        /*
                        if (audio.SyncToAudio())// this in theory will reduce skipping, setting to zero should reduce some skipping, while setting it to one should reduce it completely but often seems to just make everything sound very depressing :P
                        {
                            if (cpu.APU.curFPS > 0)
                                cpu.APU.curFPS--;
                        }
                        else if (cpu.APU.curFPS < cpu.APU.FPS)
                            cpu.APU.curFPS++;
                        cpu.APU.SetFPS(cpu.APU.curFPS);
                        */
                        if (stopWAVToolStripMenuItem.Enabled)
                        {
                            cpu.APU.SetFPS(cpu.APU.FPS);
                            wavRecorder.AddSamples(cpu.APU.output, cpu.APU.outputPtr);
                        }
                        if (stopAVSToolStripMenuItem.Enabled)
                        {
                            cpu.APU.SetFPS(cpu.APU.FPS);
                            avsRecorder.AddFrame(cpu.PPU.screen, cpu.APU.output, cpu.APU.outputPtr);
                        }
                        audio.MainLoop(cpu.APU.outputPtr, rewinding);
                    }
                    if (frame % frameSkipper == 0)//draw every n-th frame during turbo
                    {
                        if (cpu.nsfPlayer)
                            nsfScreen.ReDraw();
                        renderer.MainLoop(true);
                    }
                }
                else
                {
                    if (input != null)
                        input.MainLoop();
                    if (renderer != null)
                        renderer.MainLoop(false);
                    if (cpu != null && cpu.debug.debugInterrupt && (!debugger.updated || !debugger.smartUpdate))
                    {
                        debugger.smartUpdate = true;
                        message = "Breakpoint";
                        messageDuration = 1;
                        debugger.UpdateDebug();
                        cpu.APU.SetFPS(cpu.APU.FPS);
                    }
                    Thread.Sleep(100);
                }
                if (state != SystemState.SystemPause && this.frame++ % this.frameSkipper == 0)
                {
                    if (state != SystemState.Paused)
                        this.messageDuration--;
                    else
                        this.frame--;
                    if (cpu != null && cpu.debug.debugInterrupt)
                        messageDuration = 1;
                }
                Application.DoEvents();
            }
        }

        private unsafe void RunCPU()
        {
            if (this.storeState)
            {
                this.storeState = false;
                saveSlots[quickSaveSlot] = cpu.StateSave();
                Directory.CreateDirectory(config["savestateDir"]);
                Stream stream = new FileStream(Path.Combine(config["savestateDir"], cpu.rom.fileName + ".s" + quickSaveSlot.ToString("D2")), FileMode.Create, FileAccess.Write, FileShare.None);
                saveSlots[quickSaveSlot].stateStream.WriteTo(stream);
                stream.Close();
                this.message = "State " + quickSaveSlot.ToString() + " Saved";
                this.messageDuration = 90;
            }
            if (this.loadState)
            {
                this.loadState = false;
                if (saveSlots[quickSaveSlot].isStored)
                {
                    cpu.StateLoad(saveSlots[quickSaveSlot]);
                    this.message = "State " + quickSaveSlot.ToString() + " Loaded";
                }
                else
                {
                    message = "Empty Save Slot";
                }
                messageDuration = 90;
            }
            player1.a = (player1Pressed.a && !controlStrobe) || (player1Pressed.a && (frame % 2) == 0);
            player1.b = (player1Pressed.b && !controlStrobe) || (player1Pressed.b && (frame % 2) == 0);
            player1.select = (player1Pressed.select && !controlStrobe) || (player1Pressed.select && (frame % 2) == 0);
            player1.start = (player1Pressed.start && !controlStrobe) || (player1Pressed.start && (frame % 2) == 0);
            player1.up = (player1Pressed.up && !controlStrobe) || (player1Pressed.up && (frame % 2) == 0);
            player1.down = (player1Pressed.down && !controlStrobe) || (player1Pressed.down && (frame % 2) == 0);
            player1.left = (player1Pressed.left && !controlStrobe) || (player1Pressed.left && (frame % 2) == 0);
            player1.right = (player1Pressed.right && !controlStrobe) || (player1Pressed.right && (frame % 2) == 0);
            player2.a = (player2Pressed.a && !controlStrobe) || (player2Pressed.a && (frame % 2) == 0);
            player2.b = (player2Pressed.b && !controlStrobe) || (player2Pressed.b && (frame % 2) == 0);
            player2.select = (player2Pressed.select && !controlStrobe) || (player2Pressed.select && (frame % 2) == 0);
            player2.start = (player2Pressed.start && !controlStrobe) || (player2Pressed.start && (frame % 2) == 0);
            player2.up = (player2Pressed.up && !controlStrobe) || (player2Pressed.up && (frame % 2) == 0);
            player2.down = (player2Pressed.down && !controlStrobe) || (player2Pressed.down && (frame % 2) == 0);
            player2.left = (player2Pressed.left && !controlStrobe) || (player2Pressed.left && (frame % 2) == 0);
            player2.right = (player2Pressed.right && !controlStrobe) || (player2Pressed.right && (frame % 2) == 0);
            if (player1A.on)
                player1.a = player1A.count++ % player1A.freq == 0;
            if (player1B.on)
                player1.b = player1B.count++ % player1B.freq == 0;
            if (player2A.on)
                player2.a = player2A.count++ % player2A.freq == 0;
            if (player2B.on)
                player2.b = player2B.count++ % player2B.freq == 0;
            if (playMovie)
            {
                playMovie = !Fm2Reader();
                if (fm2Reset)
                {
                    cpu.Power();
                    fm2Reset = false;
                }
                if (!playMovie)
                {
                    this.playMovieToolStripMenuItem.Text = "Play Movie";
                    message = "Playback Ended";
                    messageDuration = 90;
                }
            }
            if (rewindingEnabled)
            {
                if (rewinding)
                {

                    if (frame % ((saveBufferFreq == 1 ? 2 : saveBufferFreq) / 2) == 0)
                    {
                        if (saveBufferAvaliable != 0)
                        {
                            saveBufferAvaliable--;
                            saveBufferCounter--;
                            if (saveBufferCounter < 0)
                                saveBufferCounter = ((60 / saveBufferFreq) * saveBufferSeconds) - 1;

                        }
                        saveSafeRewind = true;
                    }
                    if (saveSafeRewind)
                    {
                        cpu.StateLoad(saveBuffer[saveBufferCounter]);
                        moviePtr = saveBuffer[saveBufferCounter].frame;
                    }
                }
                else
                {
                    saveSafeRewind = false;
                    if (frame % saveBufferFreq == 0)
                    {
                        saveBuffer[saveBufferCounter] = cpu.StateSave();
                        saveBuffer[saveBufferCounter].frame = moviePtr;
                        saveBufferCounter++;
                        if (saveBufferCounter >= ((60 / saveBufferFreq) * saveBufferSeconds))
                            saveBufferCounter = 0;
                        if (saveBufferAvaliable != ((60 / saveBufferFreq) * saveBufferSeconds))
                            saveBufferAvaliable++;
                    }

                }
            }
            movie[moviePtr] = PlayerToByte(player1);
            moviePtr++;
            UpdateFramerate();
            if (netPlay)
            {
                netClient.SendInput(PlayerToByte(this.player1));
                cpu.Start(ByteToPlayer(netClient.player1), ByteToPlayer(netClient.player2), (this.frame % this.frameSkipper != 0));
            }
            else
            {
                cpu.Start(player1, player2, (this.frame % this.frameSkipper != 0));
            }
            if (this.generatePatternTables && this.frame % this.patternTableUpdate == 0)
            {
                this.patternTablePreview.UpdatePatternTables(cpu.PPU.patternTables, cpu.PPU.patternTablesPalette);
                cpu.PPU.generatePatternLine = this.patternTablePreview.generateLine;
                cpu.PPU.generatePatternTables = true;
            }
            if (this.generateNameTables && this.frame % this.nameTableUpdate == 0)
            {
                cpu.PPU.generateLine = this.nameTablePreview.UpdateNameTables(cpu.PPU.nameTables);
                cpu.PPU.generateNameTables = true;
            }
            if (soundVis != null && soundVis.Visible)
                soundVis.Update(cpu);
            if (memoryVis != null && memoryVis.Visible)
                memoryVis.Update(cpu);
        }

        public void Initialize()
        {
            this.appPath = Path.GetDirectoryName(Application.ExecutablePath);
            Directory.SetCurrentDirectory(appPath);
            config = new EmuConfig("Emu-o-Tron.cfg");
            this.LoadKeys();
            if (!File.Exists(this.config["palette"]))
                this.config["palette"] = config.defaults["palette"];
            FileStream palFile = File.OpenRead(this.config["palette"]);
            for (int i = 0; i < 0x40; i++)
                this.colorChart[i] = (uint)((0xFF << 24) | (palFile.ReadByte() << 16) | (palFile.ReadByte() << 8) | palFile.ReadByte());
            if (palFile.Length > 0x40 * 3) //shitty hack for vs palette because im LAZY
            {
                uint[] vsColor = new uint[0x200];
                for (int i = 0; palFile.Position < palFile.Length; i++)
                {
                    vsColor[i] = colorChart[palFile.ReadByte()];
                }
                colorChart = vsColor;
            }
            palFile.Close();
            CreateEmphasisTables();
            if (this.config["showFPS"] == "1")
            {
                this.showFPSToolStripMenuItem.Checked = true;
                this.showFPS = true;
            }
            else
            {
                this.showFPSToolStripMenuItem.Checked = false;
                this.showFPS = false;
            }
            if (this.config["showInput"] == "1")
            {
                this.showInputToolStripMenuItem.Checked = true;
                this.showInput = true;
            }
            else
            {
                this.showInputToolStripMenuItem.Checked = false;
                this.showInput = false;
            }
            this.smoothOutputToolStripMenuItem.Checked = (this.config["smoothOutput"] == "1");
            this.spritesToolStripMenuItem.Checked = (config["displaySprites"] == "1");
            this.backgroundToolStripMenuItem.Checked = (config["displayBG"] == "1");
            this.spriteLimitToolStripMenuItem.Checked = (config["disableSpriteLimit"] == "1");
            this.openPaletteDialog.InitialDirectory = Path.GetFullPath(this.config["paletteDir"]);
            this.openMovieDialog.InitialDirectory = Path.GetFullPath(this.config["movieDir"]);
            this.saveMovie.InitialDirectory = Path.GetFullPath(this.config["movieDir"]);
            this.openFile.InitialDirectory = Path.GetFullPath(this.config["romPath1"]);
            this.enableSoundToolStripMenuItem.Checked = (config["sound"] == "1");
            this.nTSCToolStripMenuItem.Checked = (SystemType)Convert.ToInt32(config["region"]) == SystemType.NTSC;
            this.pALToolStripMenuItem.Checked = (SystemType)Convert.ToInt32(config["region"]) == SystemType.PAL;
            for(int i = 1; i <= 5; i++)
                if(this.config["romPath" + i.ToString()] != "")
                    this.openFile.CustomPlaces.Add(this.config["romPath" + i.ToString()]);
            this.LoadRecentFiles();
            this.Width = Convert.ToInt32(this.config["width"]);
            this.Height = Convert.ToInt32(this.config["height"]);
            config["rawNESPalette"] = "0";
            switch (this.config["scaler"].ToLower())
            {
                default:
                case "sizable":
                case "sizeable":
                    sizeableToolStripMenuItem.Checked = true;
                    this.imageScaler = new Sizeable();
                    break;
                case "1x":
                    xToolStripMenuItem.Checked = true;
                    this.imageScaler = new NearestNeighbor1x();
                    break;
                case "2x":
                    xToolStripMenuItem1.Checked = true;
                    this.imageScaler = new NearestNeighbor2x();
                    break;
                case "scale2x":
                    scale2xToolStripMenuItem.Checked = true;
                    this.imageScaler = new Scale2x();
                    break;
                case "scale3x":
                    scale3xToolStripMenuItem.Checked = true;
                    this.imageScaler = new Scale3x();
                    break;
                case "fill":
                    fillToolStripMenuItem.Checked = true;
                    this.imageScaler = new Fill();
                    break;
                case "tv":
                    tVAspectToolStripMenuItem.Checked = true;
                    this.imageScaler = new TVAspect();
                    break;
                case "hq2x":
                    hQ2xToolStripMenuItem.Checked = true;
                    this.imageScaler = new HQ2x();
                    break;
                case "phosphor2x":
                    this.imageScaler = new Phosphor2x();
                    break;
                case "phosphor3x":
                    this.imageScaler = new Phosphor3x();
                    break;
                case "ntsc":
                    this.imageScaler = new NTSC();
                    config["rawNESPalette"] = "1";
                    break;
            }

            PrepareScaler();
            rewindingEnabled = this.config["rewindEnabled"] == "1" ? true : false;
            saveBufferFreq = Convert.ToInt32(this.config["rewindBufferFreq"]);
            saveBufferSeconds = Convert.ToInt32(this.config["rewindBufferSeconds"]);
            saveBuffer = new SaveState[(60 / saveBufferFreq) * saveBufferSeconds];

            player1A.freq = 2;
            player1A.count = 1;
            player1B.freq = 2;
            player1B.count = 1;
            player2A.freq = 2;
            player2A.count = 1;
            player2B.freq = 2;
            player2B.count = 1;
            state = SystemState.Empty;
            surfaceControl.Visible = false;

            volume.master = Convert.ToInt32(config["volume"]) / 100f;
            volume.square1 = 1;
            volume.square2 = 1;
            volume.triangle = 1;
            volume.noise = 1;
            volume.dmc = 1;
            volume.external = 1;
            if (this.romPath != "")
                this.OpenFile(romPath);
        }

        private void LoadSaveStateFiles()
        {
            saveSlots = new SaveState[10];
            for (int i = 0; i < 10; i++)
            {
                if (File.Exists(Path.Combine(config["savestateDir"], cpu.rom.fileName + ".s" + i.ToString("D2"))))
                {
                    FileStream stream = new FileStream(Path.Combine(config["savestateDir"], cpu.rom.fileName + ".s" + i.ToString("D2")), FileMode.Open, FileAccess.Read, FileShare.Read);
                    SaveState newstate = new SaveState();
                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, (int)stream.Length);
                    newstate.stateStream = new MemoryStream(buffer);
                    newstate.isStored = true;
                    saveSlots[i] = newstate;
                    stream.Close();
                }

            }
        }

        private void CreateEmphasisTables()
        {
            byte finalRed;
            byte finalGreen;
            byte finalBlue;
            double emphasis = 0.1;
            double demphasis = 0.25;
            for (int i = 1; i < 0x08; i++)
            {
                double blue = 1;
                double green = 1;
                double red = 1;
                if ((i & 0x01) != 0)
                {
                    red += emphasis;
                    green -= demphasis;
                    blue -= demphasis;
                }
                if ((i & 0x02) != 0)
                {
                    green += emphasis;
                    red -= demphasis;
                    blue -= demphasis;
                }
                if ((i & 0x04) != 0)
                {
                    blue += emphasis;
                    green -= demphasis;
                    red -= demphasis;
                }
                red = (red < 0) ? 0 : red;
                green = (green < 0) ? 0 : green;
                blue = (blue < 0) ? 0 : blue;
                for (int j = 0; j < 0x40; j++)
                {
                    finalRed = Math.Round(((colorChart[j] >> 16) & 0xFF) * red) > 0xFF ? (byte)0xFF : (byte)Math.Round(((colorChart[j] >> 16) & 0xFF) * red);
                    finalGreen = Math.Round(((colorChart[j] >> 8) & 0xFF) * green) > 0xFF ? (byte)0xFF : (byte)Math.Round(((colorChart[j] >> 8) & 0xFF) * green);
                    finalBlue = Math.Round(((colorChart[j]) & 0xFF) * blue) > 0xFF ? (byte)0xFF : (byte)Math.Round(((colorChart[j]) & 0xFF) * blue);
                    colorChart[j | (i << 6)] = (uint)((0xFF << 24) | (finalRed << 16) | (finalGreen << 8) | finalBlue); 
                }
            }
        }

        private void StartEmu()
        {
            bool logState = false;
            if (this.cpu != null)
                logState = this.cpu.debug.logging;
            if (renderer != null)
                renderer.Destroy();
            if (audio != null)
                audio.Destroy();
            if (input != null)
                input.Destroy();
            if (config["audio"] == "DXS") //DirectSound has some issues with unusual samplerates
                config["sampleRate"] = 48000.ToString();
            try
            {
                if (Path.GetExtension(romPath).ToLower() == ".fds")
                    this.cpu = new NESCore((SystemType)Convert.ToInt32(config["region"]), config["fdsBios"], this.romPath, this.appPath, Convert.ToInt32(this.config["sampleRate"]));
                else if (Path.GetExtension(romPath).ToLower() == ".nsf")
                    this.cpu = new NESCore(this.romPath, Convert.ToInt32(this.config["sampleRate"]));
                else
                    this.cpu = new NESCore((SystemType)Convert.ToInt32(config["region"]), this.romPath, this.appPath, Convert.ToInt32(this.config["sampleRate"]));
            }
            catch (BadHeaderException e)
            {
                if (MessageBox.Show("File appears to be invalid. Attempt load anyway?", "Error", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    if (Path.GetExtension(romPath).ToLower() == ".fds")
                        this.cpu = new NESCore((SystemType)Convert.ToInt32(config["region"]), config["fdsBios"], this.romPath, this.appPath, Convert.ToInt32(this.config["sampleRate"]), true);
                    else if (Path.GetExtension(romPath).ToLower() == ".nsf")
                        this.cpu = new NESCore(this.romPath, Convert.ToInt32(this.config["sampleRate"]), true);
                    else
                        this.cpu = new NESCore((SystemType)Convert.ToInt32(config["region"]), this.romPath, this.appPath, Convert.ToInt32(this.config["sampleRate"]), true);
                else
                    throw (e);
            }
            catch (FDSBiosException e)
            {
                MessageBox.Show("FDS BIOS image (" + config["fdsBios"] + ") not found.");
                return;
            }
            this.frame = 0;
            moviePtr = 0;
            this.saveBufferAvaliable = 0;
            this.cpu.debug.logging = logState;
            this.cpu.PPU.displayBG = (config["displayBG"] == "1");
            this.cpu.PPU.displaySprites = (config["displaySprites"] == "1");
            this.cpu.PPU.enforceSpriteLimit = !(config["disableSpriteLimit"] == "1");
            if (config["simulatedPalette"] == "1")
            {
                NTSCFilter.gamma = double.Parse(config["gamma"]);
                NTSCFilter.hue = double.Parse(config["hue"]);
                NTSCFilter.sat = double.Parse(config["saturation"]);
                NTSCFilter.brightness = double.Parse(config["brightness"]);
                this.cpu.PPU.colorChart = NTSCFilter.NesPalette;
            }
            else
            {
                this.cpu.PPU.colorChart = this.colorChart;
            }
            if (config["rawNESPalette"] == "1")
            {
                for (uint i = 0; i < 0x200; i++)
                    this.cpu.PPU.colorChart[i] = i;
            }
            this.cpu.APU.mute = !(config["sound"] == "1");
            this.cpu.APU.volume = volume;
            this.LoadGame();
            this.LoadSaveStateFiles();
            this.cpu.gameGenieCodeNum = this.gameGenieCodeCount;
            this.cpu.gameGenieCodes = this.gameGenieCodes;
            this.Text = this.cpu.rom.fileName + " - Emu-o-Tron";
            this.state = SystemState.Playing;
            this.surfaceControl.Visible = true;
            if (this.cpu.rom.vsUnisystem)
            {
                this.state = SystemState.SystemPause;
                DipDialog DipDiag = new DipDialog();
                DipDiag.FormClosing += new FormClosingEventHandler(DipDiag_FormClosing);
                DipDiag.ShowDialog();
            }
            if (debugger != null)
                debugger.Close();
            debugger = new Debugger(cpu.debug);
            debugger.UpdateDebug();
            ejectDiskToolStripMenuItem.DropDownItems.Clear();
            ejectDiskToolStripMenuItem.Text = "Eject Disk";
            ejectDiskToolStripMenuItem.Visible = (cpu.GetSideCount() != 0);
            cpu.SetControllers((ControllerType)Enum.Parse(typeof(ControllerType), config["portOne"]), (ControllerType)Enum.Parse(typeof(ControllerType), config["portTwo"]), (ControllerType)Enum.Parse(typeof(ControllerType), config["expansion"]), config["fourScore"] == "1", config["filterIllegalInput"] == "1");

            if (cpu.nsfPlayer)
                nsfScreen = new NSFScreen(cpu);

            switch (config["renderer"])
            {
#if NO_DX
                    
                default:
                case "GDI":
                    config["renderer"] = "GDI";
                    renderer = new GDIRenderer(surfaceControl, imageScaler, cpu.PPU.screen, smoothOutputToolStripMenuItem.Checked);
                    break;
#else
                case "GDI":
                    renderer = new GDIRenderer(surfaceControl, imageScaler, cpu.PPU.screen, smoothOutputToolStripMenuItem.Checked);
                    break;
                default:
                case "DX9":
                    config["renderer"] = "DX9";
                    renderer = new DX9Renderer(surfaceControl, imageScaler, cpu.PPU.screen, smoothOutputToolStripMenuItem.Checked);
                    break;
                case "DX10":
                    renderer = new DX10Renderer(surfaceControl, imageScaler, cpu.PPU.screen, smoothOutputToolStripMenuItem.Checked);
                    break;
#endif
                case "OGL":
                    renderer = new OpenGLRenderer(surfaceControl, imageScaler, cpu.PPU.screen, smoothOutputToolStripMenuItem.Checked);
                    break;
                case "Null":
                    renderer = new NullRenderer();
                    break;
            }
            renderer.DrawMessageEvent += new EventHandler(renderer_DrawMessageEvent);
            renderer.Create();

            switch (config["audio"])
            {
                case "OAL":
                    audio = new OpenALAudio(cpu.APU.sampleRate, cpu.APU.output, Convert.ToInt32(config["volume"]) / 100f);
                    break;

#if NO_DX
                default:
                case "Null":
                    config["audio"] = "Null";
                    audio = new NullAudio(cpu.APU.sampleRate);
                    break;
#else
                default:
                case "XA2":
                    config["audio"] = "XA2";
                    audio = new XA2Audio(cpu.APU.sampleRate, cpu.APU.output, Convert.ToInt32(config["volume"]) / 100f);
                    break;
                case "DXS":
                    audio = new DXSAudio(cpu.APU.sampleRate, cpu.APU.output, Convert.ToInt32(config["volume"]) / 100f, this.Handle);
                    break;
                case "Null":
                    audio = new NullAudio(cpu.APU.sampleRate);
                    break;
#endif
            }
            audio.Create();

            switch (config["input"])
            {
                default:
                case "Win":
                    config["input"] = "Win";
                    input = new WinInput(this, surfaceControl);
                    break;
#if !NO_DX
                case "DX":
                    input = new DXInput(this);
                    break;
                case "XIn":
                    input = new XInInput();
                    break;
#endif
                case "Null":
                    input = new NullInput();
                    break;
            }
            input.Create();
            input.InputEvent += new InputHandler(input_InputEvent);
            input.InputScalerEvent += new InputScalerHandler(input_InputScalerEvent);

        }

        void input_InputScalerEvent(EmuKeys key, int value)
        {
            switch (key)
            {
                case EmuKeys.MouseX:
                    Point tmpPointX = surfaceControl.PointToClient(new Point(value, 0));
                    tmpPointX.X = (int)((256 * tmpPointX.X) / (surfaceControl.Width * 1.0));
                    if (tmpPointX.X < 0)
                        tmpPointX.X = 0;
                    else if (tmpPointX.X >= 256)
                        tmpPointX.X = 256 - 1;
                    player1.x = (byte)tmpPointX.X;
                    player2.x = player1.x;
                    break;
                case EmuKeys.MouseY:
                    Point tmpPointY = surfaceControl.PointToClient(new Point(0, value));
                    tmpPointY.Y = (int)((240 * tmpPointY.Y) / (surfaceControl.Height * 1.0));
                    if (tmpPointY.Y < 0)
                        tmpPointY.Y = 0;
                    else if (tmpPointY.Y >= 240)
                        tmpPointY.Y = 240 - 1;
                    player1.y = (byte)tmpPointY.Y;
                    player2.y = player1.y;
                    break;
            }
        }

        void input_InputEvent(EmuKeys key, bool pressed)
        {
            if (key == keyBindings.Player1A)
            {
                player1Pressed.a = pressed;
            }
            else if (key == keyBindings.Player1TurboA)
            {
                player1A.on = pressed;
                if(!pressed)
                    player1A.count = 1;
            }
            else if (key == keyBindings.Player1B)
            {
                player1Pressed.b = pressed;
            }
            else if (key == keyBindings.Player1TurboB)
            {
                player1B.on = pressed;
                if (!pressed)
                    player1B.count = 1;
            }
            else if (key == keyBindings.Player1Select)
            {
                player1Pressed.select = pressed;
            }
            else if (key == keyBindings.Player1Start)
            {
                player1Pressed.start = pressed;
            }
            else if (key == keyBindings.Player1Up)
            {
                player1Pressed.up = pressed;
            }
            else if (key == keyBindings.Player1Down)
            {
                player1Pressed.down = pressed;
            }
            else if (key == keyBindings.Player1Left)
            {
                player1Pressed.left = pressed;
                if (cpu != null && !pressed)
                    cpu.NSFPreviousSong();
            }
            else if (key == keyBindings.Player1Right)
            {
                player1Pressed.right = pressed;
                if (cpu != null && !pressed)
                    cpu.NSFNextSong();
            }
            else if (key == EmuKeys.Mouse1)
            {
                player1.triggerPulled = pressed;
                player2.triggerPulled = pressed;
            }
            else if (key == keyBindings.Player2A)
            {
                player2Pressed.a = pressed;
            }
            else if (key == keyBindings.Player2TurboA)
            {
                player2A.on = pressed;
                if (!pressed)
                    player2A.count = 1;
            }
            else if (key == keyBindings.Player2B)
            {
                player2Pressed.b = pressed;
            }
            else if (key == keyBindings.Player2TurboB)
            {
                player2B.on = pressed;
                if (!pressed)
                    player2B.count = 1;
            }
            else if (key == keyBindings.Player2Select)
            {
                player2Pressed.select = pressed;
            }
            else if (key == keyBindings.Player2Start)
            {
                player2Pressed.start = pressed;
            }
            else if (key == keyBindings.Player2Up)
            {
                player2Pressed.up = pressed;
            }
            else if (key == keyBindings.Player2Down)
            {
                player2Pressed.down = pressed;
            }
            else if (key == keyBindings.Player2Left)
            {
                player2Pressed.left = pressed;
            }
            else if (key == keyBindings.Player2Right)
            {
                player2Pressed.right = pressed;
            }
            else if (key == EmuKeys.Q)
            {
                controlStrobe = pressed;
            }
            else if (key == keyBindings.SaveState)
            {
                if (!pressed)
                    this.storeState = true;
            }
            else if (key == keyBindings.LoadState)
            {
                if (!pressed)
                    this.loadState = true;
            }
            else if (key == keyBindings.Rewind)
            {
                rewinding = pressed;
            }
            else if (key == keyBindings.FastForward)
            {
                frameSkipper = pressed ? maxFrameSkip : 1;
            }
            else if (key == EmuKeys.F2)
            {
                player1.coin = pressed;
            }
            else if (key == EmuKeys.F3)
            {
                player2.coin = pressed;
            }
            else if (key == keyBindings.Pause)
            {
                if (!pressed)
                {
                    if (state == SystemState.Playing)
                        state = SystemState.Paused;
                    else if (state == SystemState.Paused)
                        state = SystemState.Playing;
                    this.message = "Paused";
                    this.messageDuration = 1;
                }
            }
            else if (key == keyBindings.Reset)
            {
                if (!pressed)
                {
                    this.SaveGame();
                    moviePtr = 0;
                    this.cpu.Reset();
                    this.LoadGame();
                    this.message = "Reset";
                    this.messageDuration = 90;
                }
            }
            else if (key == keyBindings.Power)
            {
                if (!pressed)
                {
                    this.SaveGame();
                    moviePtr = 0;
                    this.cpu.Power();
                    this.LoadGame();
                    this.message = "Power";
                    this.messageDuration = 90;
                }
            }
            else if (key == EmuKeys.LeftBracket)
            {
                if (!pressed)
                {
                    quickSaveSlot--;
                    if (quickSaveSlot < 0)
                        quickSaveSlot = 9;
                    this.message = "Save Slot " + quickSaveSlot.ToString();
                    this.messageDuration = 90;
                }

            }
            else if (key == EmuKeys.RightBracket)
            {

                if (!pressed)
                {
                    quickSaveSlot++;
                    if (quickSaveSlot > 9)
                        quickSaveSlot = 0;
                    this.message = "Save Slot " + quickSaveSlot.ToString();
                    this.messageDuration = 90;
                }

            }
            else if (key == EmuKeys.Backslash)
            {
                if (!pressed)
                {
                    if (state == SystemState.Paused)
                        state = SystemState.Playing;
                    frameAdvance = true;
                    this.message = "Paused";
                    this.messageDuration = 2;
                }
            }
            else if (key == EmuKeys.Grave)
            {
                if (!pressed)
                {
                    if (rewindingEnabled)
                    {
                        if (state == SystemState.Paused)
                            state = SystemState.Playing;
                        frameDevance = true;
                        rewinding = true;
                        this.message = "Paused";
                        this.messageDuration = 2;
                    }
                }
            }
        }

        private void SaveGame()
        {
            Directory.CreateDirectory(this.config["sramDir"]);
            if (this.cpu != null)
                if (this.cpu.rom.sRAM)
                    File.WriteAllBytes(Path.Combine(this.config["sramDir"], this.cpu.rom.fileName + ".sav"), this.cpu.GetSRAM());
        }
        private void LoadGame()
        {
            Directory.CreateDirectory(this.config["sramDir"]);
            if (this.cpu != null)
            {
                if (this.cpu.rom.sRAM)
                {
                    if (File.Exists(Path.Combine(this.config["sramDir"], this.cpu.rom.fileName + ".sav")))
                        this.cpu.SetSRAM(File.ReadAllBytes(Path.Combine(this.config["sramDir"], this.cpu.rom.fileName + ".sav")));
                }
            }
        }

        private void OpenFile(string fileName, bool addToRecent)
        {
            try
            {
                this.SaveGame();
                string extractedFileName = this.ExtractFile(fileName);
                if (Path.GetExtension(extractedFileName).ToLower() == ".ips" || Path.GetExtension(extractedFileName).ToLower() == ".ups")
                {
                    SystemState old = state;
                    state = SystemState.SystemPause;
                    MessageBox.Show("Select file to apply patch to.", "Patch ROM");
                    if (this.openFile.ShowDialog() == DialogResult.OK)
                    {
                        string prePatch = this.ExtractFile(openFile.FileName);
                        if (prePatch != "")
                            if (Path.GetExtension(extractedFileName).ToLower() == ".ips")
                                extractedFileName = RomPatching.IPSPatch(prePatch, extractedFileName, config["tmpDir"]);
                            else if (Path.GetExtension(extractedFileName).ToLower() == ".ups")
                                extractedFileName = RomPatching.UPSPatch(prePatch, extractedFileName, config["tmpDir"]);
                            else
                                extractedFileName = "";
                    }
                    else
                        extractedFileName = "";
                    state = old;
                }
                if (extractedFileName != "")
                {
                    this.romPath = extractedFileName;
                    this.StartEmu();
                    if (addToRecent)
                        this.AddRecentFile(fileName);
                }
            }
            catch (BadHeaderException e)
            {
                SystemState old = state;
                state = SystemState.SystemPause;
                if (this.openFile.ShowDialog() == DialogResult.OK)
                {
                    state = old;
                    this.OpenFile(this.openFile.FileName);

                }
            }
        }

        private void OpenFile(string fileName)
        {
            this.OpenFile(fileName, true);
        }

        private void loadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.openFile.ShowDialog() == DialogResult.OK)
            {
                this.OpenFile(this.openFile.FileName);
                this.openFile.InitialDirectory = Path.GetDirectoryName(this.openFile.FileName);
            }
        }

        private void Program_DragDrop(object sender, DragEventArgs e)
        {
            this.OpenFile(((string[])e.Data.GetData(DataFormats.FileDrop))[0]);
        }

        private void Program_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop, false) == true)
                e.Effect = DragDropEffects.All;
        }

        private string ExtractFile(string fileName)
        {
            if (isMono)
                return fileName;
            SystemState old = state;
            state = SystemState.SystemPause;
            string ext = Path.GetExtension(fileName).ToLower();
            if (ext == ".7z" || ext == ".zip" || ext == ".rar")
            {
                string sevenZ = this.config["7z"];
                if (IntPtr.Size == 8)
                    sevenZ = this.config["7z64"]; //replace this with installer logic maybe?
                SevenZipFormat Format = new SevenZipFormat(sevenZ);
                KnownSevenZipFormat fileType;
                switch (ext)
                {
                    default:
                    case ".7z":
                        fileType = KnownSevenZipFormat.SevenZip;
                        break;
                    case ".zip":
                        fileType = KnownSevenZipFormat.Zip;
                        break;
                    case ".rar":
                        fileType = KnownSevenZipFormat.Rar;
                        break;
                }
                IInArchive Archive = Format.CreateInArchive(SevenZipFormat.GetClassIdFromKnownFormat(fileType));
                try
                {
                    InStreamWrapper ArchiveStream = new InStreamWrapper(File.OpenRead(fileName));
                    ulong checkPos = 32 * 1024;
                    Archive.Open(ArchiveStream, ref checkPos, null);
                    uint count = Archive.GetNumberOfItems();
                    string[] archiveContent = new string[count];
                    for (uint i = 0; i < count; i++)
                    {
                        PropVariant name = new PropVariant();
                        Archive.GetProperty(i, ItemPropId.kpidPath, ref name);
                        archiveContent[i] = name.GetObject().ToString();
                    }
                    if (count == 1)
                    {
                        fileName = Path.Combine(this.config["tmpDir"], Path.GetFileNameWithoutExtension(archiveContent[0]) + ".tmp" + Path.GetExtension(archiveContent[0]));
                        Archive.Extract(new uint[] { 0 }, 1, 0, new ArchiveCallback(0, fileName));
                    }
                    else
                    {
                        ArchiveViewer viewer = new ArchiveViewer(archiveContent);
                        if (viewer.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            fileName = Path.Combine(this.config["tmpDir"], Path.GetFileNameWithoutExtension(archiveContent[viewer.selectedFile]) + ".tmp" + Path.GetExtension(archiveContent[0]));
                            Archive.Extract(new uint[] { (uint)viewer.selectedFile }, 1, 0, new ArchiveCallback((uint)viewer.selectedFile, fileName));
                        }
                        else
                        {
                            fileName = "";
                        }
                    }
                    ArchiveStream.Dispose();
                }
                finally
                {
                    Archive.Close();
                    Marshal.ReleaseComObject(Archive);
                }
                Format.Dispose();
            }
            state = old;
            return fileName;
        }

        private void UpdateFramerate()
        {
            frames++;
            if (Math.Abs(Environment.TickCount - lastTickCount) > 1000)
            {
                lastFrameRate = frames * 1000 / Math.Abs(Environment.TickCount - lastTickCount);
                lastTickCount = Environment.TickCount;
                frames = 0;
            }
        }

        private uint GetScreenCRC(uint[,] scanlines)
        {
            uint crc = 0xFFFFFFFF;
            for (int y = 0; y < 240; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    crc = CRC32.crc32_adjust(crc, (byte)(scanlines[y, x] & 0xFF));
                    crc = CRC32.crc32_adjust(crc, (byte)((scanlines[y, x] >> 8) & 0xFF));
                    crc = CRC32.crc32_adjust(crc, (byte)((scanlines[y, x] >> 16) & 0xFF));
                    crc = CRC32.crc32_adjust(crc, (byte)((scanlines[y, x] >> 24) & 0xFF));
                }
            }
            crc ^= 0xFFFFFFFF;
            return crc;
        }

        #region recent_files
        private void LoadRecentFiles()
        {
            this.recentFileMenu1.Text = "Recent Files";
            this.recentFileMenu1.Enabled = false;
            this.recentFileMenu2.Visible = false;
            this.recentFileMenu2.Enabled = false;
            this.recentFileMenu3.Visible = false;
            this.recentFileMenu3.Enabled = false;
            this.recentFileMenu4.Visible = false;
            this.recentFileMenu4.Enabled = false;
            this.recentFileMenu5.Visible = false;
            this.recentFileMenu5.Enabled = false;
            if (this.config["recentFile1"] != "")
            {
                this.recentFileMenu1.Text = Path.GetFileName(this.config["recentFile1"]);
                this.recentFileMenu1.Enabled = true;
            }
            if (this.config["recentFile2"] != "")
            {
                this.recentFileMenu2.Text = Path.GetFileName(this.config["recentFile2"]);
                this.recentFileMenu2.Visible = true;
                this.recentFileMenu2.Enabled = true;
            }
            if (this.config["recentFile3"] != "")
            {
                this.recentFileMenu3.Text = Path.GetFileName(this.config["recentFile3"]);
                this.recentFileMenu3.Visible = true;
                this.recentFileMenu3.Enabled = true;
            }
            if (this.config["recentFile4"] != "")
            {
                this.recentFileMenu4.Text = Path.GetFileName(this.config["recentFile4"]);
                this.recentFileMenu4.Visible = true;
                this.recentFileMenu4.Enabled = true;
            }
            if (this.config["recentFile5"] != "")
            {
                this.recentFileMenu5.Text = Path.GetFileName(this.config["recentFile5"]);
                this.recentFileMenu5.Visible = true;
                this.recentFileMenu5.Enabled = true;
            }
        }
        private void AddRecentFile(string fileName)
        {
            this.recentFileMenu1.Text = "Recent Files";
            this.recentFileMenu1.Enabled = false;
            this.recentFileMenu2.Visible = false;
            this.recentFileMenu2.Enabled = false;
            this.recentFileMenu3.Visible = false;
            this.recentFileMenu3.Enabled = false;
            this.recentFileMenu4.Visible = false;
            this.recentFileMenu4.Enabled = false;
            this.recentFileMenu5.Visible = false;
            this.recentFileMenu5.Enabled = false;
            if (fileName == this.config["recentFile1"])
            {
                this.config["recentFile5"] = this.config["recentFile5"];
                this.config["recentFile4"] = this.config["recentFile4"];
                this.config["recentFile3"] = this.config["recentFile3"];
                this.config["recentFile2"] = this.config["recentFile2"];
                this.config["recentFile1"] = fileName;
            }
            else if (fileName == this.config["recentFile2"])
            {
                this.config["recentFile5"] = this.config["recentFile5"];
                this.config["recentFile4"] = this.config["recentFile4"];
                this.config["recentFile3"] = this.config["recentFile3"];
                this.config["recentFile2"] = this.config["recentFile1"];
                this.config["recentFile1"] = fileName;
            }
            else if (fileName == this.config["recentFile3"])
            {
                this.config["recentFile5"] = this.config["recentFile5"];
                this.config["recentFile4"] = this.config["recentFile4"];
                this.config["recentFile3"] = this.config["recentFile2"];
                this.config["recentFile2"] = this.config["recentFile1"];
                this.config["recentFile1"] = fileName;
            }
            else if (fileName == this.config["recentFile4"])
            {
                this.config["recentFile5"] = this.config["recentFile5"];
                this.config["recentFile4"] = this.config["recentFile3"];
                this.config["recentFile3"] = this.config["recentFile2"];
                this.config["recentFile2"] = this.config["recentFile1"];
                this.config["recentFile1"] = fileName;
            }
            else
            {
                this.config["recentFile5"] = this.config["recentFile4"];
                this.config["recentFile4"] = this.config["recentFile3"];
                this.config["recentFile3"] = this.config["recentFile2"];
                this.config["recentFile2"] = this.config["recentFile1"];
                this.config["recentFile1"] = fileName;
            }
            if (this.config["recentFile1"] != "")
            {
                this.recentFileMenu1.Text = Path.GetFileName(this.config["recentFile1"]);
                this.recentFileMenu1.Enabled = true;
            }
            if (this.config["recentFile2"] != "")
            {
                this.recentFileMenu2.Text = Path.GetFileName(this.config["recentFile2"]);
                this.recentFileMenu2.Visible = true;
                this.recentFileMenu2.Enabled = true;
            }
            if (this.config["recentFile3"] != "")
            {
                this.recentFileMenu3.Text = Path.GetFileName(this.config["recentFile3"]);
                this.recentFileMenu3.Visible = true;
                this.recentFileMenu3.Enabled = true;
            }
            if (this.config["recentFile4"] != "")
            {
                this.recentFileMenu4.Text = Path.GetFileName(this.config["recentFile4"]);
                this.recentFileMenu4.Visible = true;
                this.recentFileMenu4.Enabled = true;
            }
            if (this.config["recentFile5"] != "")
            {
                this.recentFileMenu5.Text = Path.GetFileName(this.config["recentFile5"]);
                this.recentFileMenu5.Visible = true;
                this.recentFileMenu5.Enabled = true;
            }
        }
        private void recentFileMenu1_Click(object sender, EventArgs e)
        {
            this.OpenFile(this.config["recentFile1"], false);
        }

        private void recentFileMenu2_Click(object sender, EventArgs e)
        {
            this.OpenFile(this.config["recentFile2"], false);
        }

        private void recentFileMenu3_Click(object sender, EventArgs e)
        {
            this.OpenFile(this.config["recentFile3"], false);
        }

        private void recentFileMenu4_Click(object sender, EventArgs e)
        {
            this.OpenFile(this.config["recentFile4"], false);
        }

        private void recentFileMenu5_Click(object sender, EventArgs e)
        {
            this.OpenFile(this.config["recentFile5"], false);
        }
        #endregion

        #region movies
        private bool Fm2Reader()
        {
            string line = "";
            while (line.Length == 0 || line[0] != '|')
            {
                line = "";
                bool done = false;
                while (!done)
                {
                    if (fm2File.BaseStream.Position == fm2File.BaseStream.Length)
                    {
                        fm2File.Close();
                        return true;
                    }
                    char nextChar = fm2File.ReadChar();
                    if (nextChar == '\r' || nextChar == '\n')
                        done = true;
                    else
                        line += nextChar;
                }
            }
            player1.right = line[3] != '.' && line[3] != ' ';
            player1.left = line[4] != '.' && line[4] != ' ';
            player1.down = line[5] != '.' && line[5] != ' ';
            player1.up = line[6] != '.' && line[6] != ' ';
            player1.start = line[7] != '.' && line[7] != ' ';
            player1.select = line[8] != '.' && line[8] != ' ';
            player1.b = line[9] != '.' && line[9] != ' ';
            player1.a = line[10] != '.' && line[10] != ' ';
            player2.right = line[12] != '.' && line[12] != ' ';
            player2.left = line[13] != '.' && line[13] != ' ';
            player2.down = line[14] != '.' && line[14] != ' ';
            player2.up = line[15] != '.' && line[15] != ' ';
            player2.start = line[16] != '.' && line[16] != ' ';
            player2.select = line[17] != '.' && line[17] != ' ';
            player2.b = line[18] != '.' && line[18] != ' ';
            player2.a = line[19] != '.' && line[19] != ' ';
            fm2Reset = line[1] == '1';
            return fm2File.BaseStream.Position == fm2File.BaseStream.Length;
        }

        private void openMovieToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.openMovieDialog.ShowDialog() == DialogResult.OK)
            {
                this.playMovieToolStripMenuItem.Enabled = true;
            }
        }

        private void playMovieToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.playMovie)
            {
                this.playMovie = false;
                this.fm2File.Close();
                this.playMovieToolStripMenuItem.Text = "Play Movie";
            }
            else
            {
                this.fm2File = new BinaryReader(File.OpenRead(this.openMovieDialog.FileName));
                cpu.Power();
                this.playMovie = true;
                this.playMovieToolStripMenuItem.Text = "Stop Movie";
            }
        }

        private void saveMovieToolStripMenuItem_Click(object sender, EventArgs e)
        {
            saveMovie.FileName = cpu.rom.fileName.ToString() + ".fm2";
            if (saveMovie.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                FileStream mov = File.Create(saveMovie.FileName);
                for (int i = 0; i < moviePtr; i++)
                {
                    EmuoTron.Controller inp = ByteToPlayer(movie[i]);
                    mov.WriteByte((byte)'|');
                    mov.WriteByte((byte)'0');
                    mov.WriteByte((byte)'|');
                    mov.WriteByte((byte)(inp.right ? 'R' : '.'));
                    mov.WriteByte((byte)(inp.left ? 'L' : '.'));
                    mov.WriteByte((byte)(inp.down ? 'D' : '.'));
                    mov.WriteByte((byte)(inp.up ? 'U' : '.'));
                    mov.WriteByte((byte)(inp.start ? 'T' : '.'));
                    mov.WriteByte((byte)(inp.select ? 'S' : '.'));
                    mov.WriteByte((byte)(inp.b ? 'B' : '.'));
                    mov.WriteByte((byte)(inp.a ? 'A' : '.'));
                    mov.WriteByte((byte)'|');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'.');
                    mov.WriteByte((byte)'|');
                    mov.WriteByte((byte)'|');
                    mov.WriteByte((byte)'\n');
                }
                mov.Close();
            }
        }
        #endregion

        private void loadPaletteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            PaletteConfig palConfig = new PaletteConfig(config, cpu == null ? new uint[0x200] : cpu.PPU.colorChart);
            palConfig.FormClosed += new FormClosedEventHandler(palConfig_FormClosed);
            palConfig.Show();
        }

        void palConfig_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (config["simulatedPalette"] == "1")
            {
                if (cpu != null)
                {
                    NTSCFilter.gamma = double.Parse(config["gamma"]);
                    NTSCFilter.hue = double.Parse(config["hue"]);
                    NTSCFilter.sat = double.Parse(config["saturation"]);
                    NTSCFilter.brightness = double.Parse(config["brightness"]);
                    for (int i = 0; i < 0x200; i++)
                        cpu.PPU.colorChart[i] = NTSCFilter.NESToRGB(i);
                }
            }
            else
            {
                FileStream palFile = File.OpenRead(config["palette"]);
                for (int i = 0; i < 0x40; i++)
                    this.colorChart[i] = (uint)((0xFF << 24) | (palFile.ReadByte() << 16) | (palFile.ReadByte() << 8) | palFile.ReadByte());
                if (palFile.Length > 0x40 * 3) //shitty hack for vs palette because im LAZY
                {
                    uint[] vsColor = new uint[0x200];
                    for (int i = 0; palFile.Position < palFile.Length; i++)
                    {
                        vsColor[i] = colorChart[palFile.ReadByte()];
                    }
                    colorChart = vsColor;
                }
                palFile.Close();
                CreateEmphasisTables();
                if (cpu != null)
                    this.cpu.PPU.colorChart = this.colorChart;
            }
        }

        private void gameGenieCodesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GameGenieWindow codeWindow = new GameGenieWindow();
            codeWindow.AddCodes(this.gameGenieCodes, this.gameGenieCodeCount);
            codeWindow.ShowDialog();
            GameGenie[] tmp = codeWindow.GetCodes();
            for (int i = 0; i < tmp.Length; i++)
            {
                this.gameGenieCodes[i] = tmp[i];
            }
            this.gameGenieCodeCount = tmp.Length;
            if (this.cpu != null)
            {
                this.cpu.gameGenieCodeNum = this.gameGenieCodeCount;
                this.cpu.gameGenieCodes = this.gameGenieCodes;
            }
        }

        private void aboutEmuoTronToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var about = new About();
            about.ShowDialog();
        }

        #region input_events
        private void EmuWindow_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11) //TO-DO: turn into real keybind
            {
                ToggleFullScreen();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (fullScreen)
                {
                    ToggleFullScreen();
                }
            }
            else if (e.KeyValue == 18) //This sucks dick too but I'm not sure how best to do it
            {
                if (fullScreen)
                {
                    this.menuStrip.Visible = !this.menuStrip.Visible;
                }
            }
        }
#endregion

        void renderer_DrawMessageEvent(object sender, EventArgs e)
        {
            if (messageDuration > 0)
                renderer.DrawMessage(message, DirectXEmu.Anchor.TopLeft, 0, 0);
            if (showFPS)
                renderer.DrawMessage(lastFrameRate.ToString(), DirectXEmu.Anchor.TopRight, 0, 0);
            if (config["showDebug"] == "1")
            {
                renderer.DrawMessage(frame.ToString(), DirectXEmu.Anchor.TopRight, 0, 1);
                if (cpu != null)
                {
                    uint CRC = GetScreenCRC(cpu.PPU.screen);
                    renderer.DrawMessage(CRC.ToString("X8"), DirectXEmu.Anchor.TopRight, 0, 2);
                }
            }
            if (showInput)
            {
                StringBuilder inputString = new StringBuilder(32);
                if (this.player1.up)
                    inputString.Append("^");
                else
                    inputString.Append(" ");
                if (this.player1.down)
                    inputString.Append("_");
                else
                    inputString.Append(" ");
                if (this.player1.left)
                    inputString.Append("<");
                else
                    inputString.Append(" ");
                if (this.player1.right)
                    inputString.Append(">");
                else
                    inputString.Append(" ");
                if (this.player1.start)
                    inputString.Append("*&");
                else
                    inputString.Append("  ");
                if (this.player1.select)
                    inputString.Append("*$");
                else
                    inputString.Append("  ");
                if (this.player1.a)
                    inputString.Append("A");
                else
                    inputString.Append(" ");
                if (this.player1.b)
                    inputString.Append("B");
                else
                    inputString.Append(" ");
                inputString.Append(" ");
                if (this.player2.up)
                    inputString.Append("^");
                else
                    inputString.Append(" ");
                if (this.player2.down)
                    inputString.Append("_");
                else
                    inputString.Append(" ");
                if (this.player2.left)
                    inputString.Append("<");
                else
                    inputString.Append(" ");
                if (this.player2.right)
                    inputString.Append(">");
                else
                    inputString.Append(" ");
                if (this.player2.start)
                    inputString.Append("*&");
                else
                    inputString.Append("  ");
                if (this.player2.select)
                    inputString.Append("*$");
                else
                    inputString.Append("  ");
                if (this.player2.a)
                    inputString.Append("A");
                else
                    inputString.Append(" ");
                if (this.player2.b)
                    inputString.Append("B");
                else
                    inputString.Append(" ");
                renderer.DrawMessage(inputString.ToString(), DirectXEmu.Anchor.BottomLeft, 0, 0);
            }
            if (netPlay)
            {
                if (netClient.pendingMessage != 0)
                {
                    renderer.DrawMessage(netClient.message, DirectXEmu.Anchor.TopLeft, 0, 1);
                    netClient.pendingMessage--;
                }
            }
        }

        #region keybinds
        private void keyBindingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SystemState old = state;
            state = SystemState.SystemPause;
            Keybind keyBindWindow = new Keybind(keyBindings, (ControllerType)Enum.Parse(typeof(ControllerType), config["portOne"]), (ControllerType)Enum.Parse(typeof(ControllerType), config["portTwo"]), (ControllerType)Enum.Parse(typeof(ControllerType), config["expansion"]), (config["fourScore"] == "1"), config["filterIllegalInput"] == "1", config["input"]);
            if (keyBindWindow.ShowDialog() == DialogResult.OK)
            {
                keyBindings = keyBindWindow.keys;
                config["portOne"] = keyBindWindow.portOne.ToString();
                config["portTwo"] = keyBindWindow.portTwo.ToString();
                config["expansion"] = keyBindWindow.expansion.ToString();
                config["fourScore"] = keyBindWindow.fourScore ? "1" : "0";
                config["filterIllegalInput"] = keyBindWindow.filterIllegalInput ? "1" : "0";
                if (cpu != null)
                {
                    cpu.SetControllers(keyBindWindow.portOne, keyBindWindow.portTwo, keyBindWindow.expansion, keyBindWindow.fourScore, keyBindWindow.filterIllegalInput);
                }
            }
            state = old;
        }

        private void LoadKeys()
        {
            keyBindings = new Keybinds();
            keyBindings.Player1Up = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1Up"]);
            keyBindings.Player1Down = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1Down"]);
            keyBindings.Player1Left = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1Left"]);
            keyBindings.Player1Right = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1Right"]);
            keyBindings.Player1Start = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1Start"]);
            keyBindings.Player1Select = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1Select"]);
            keyBindings.Player1A = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1A"]);
            keyBindings.Player1B = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1B"]);
            keyBindings.Player1TurboA = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1TurboA"]);
            keyBindings.Player1TurboB = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player1TurboB"]);
            keyBindings.Player2Up = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2Up"]);
            keyBindings.Player2Down = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2Down"]);
            keyBindings.Player2Left = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2Left"]);
            keyBindings.Player2Right = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2Right"]);
            keyBindings.Player2Start = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2Start"]);
            keyBindings.Player2Select = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2Select"]);
            keyBindings.Player2A = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2A"]);
            keyBindings.Player2B = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2B"]);
            keyBindings.Player2TurboA = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2TurboA"]);
            keyBindings.Player2TurboB = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["player2TurboB"]);
            keyBindings.LoadState = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["loadState"]);
            keyBindings.SaveState = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["saveState"]);
            keyBindings.Rewind = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["rewind"]);
            keyBindings.FastForward = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["fastForward"]);
            keyBindings.Pause = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["pause"]);
            keyBindings.Power = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["power"]);
            keyBindings.Reset = (EmuKeys)Enum.Parse(typeof(EmuKeys), this.config["restart"]);
        }

        private void SaveKeys()
        {
            this.config["player1Up"] = keyBindings.Player1Up.ToString();
            this.config["player1Down"] = keyBindings.Player1Down.ToString();
            this.config["player1Left"] = keyBindings.Player1Left.ToString();
            this.config["player1Right"] = keyBindings.Player1Right.ToString();
            this.config["player1Start"] = keyBindings.Player1Start.ToString();
            this.config["player1Select"] = keyBindings.Player1Select.ToString();
            this.config["player1A"] = keyBindings.Player1A.ToString();
            this.config["player1B"] = keyBindings.Player1B.ToString();
            this.config["player1TurboA"] = keyBindings.Player1TurboA.ToString();
            this.config["player1TurboB"] = keyBindings.Player1TurboB.ToString();
            this.config["player2Up"] = keyBindings.Player2Up.ToString();
            this.config["player2Down"] = keyBindings.Player2Down.ToString();
            this.config["player2Left"] = keyBindings.Player2Left.ToString();
            this.config["player2Right"] = keyBindings.Player2Right.ToString();
            this.config["player2Start"] = keyBindings.Player2Start.ToString();
            this.config["player2Select"] = keyBindings.Player2Select.ToString();
            this.config["player2A"] = keyBindings.Player2A.ToString();
            this.config["player2B"] = keyBindings.Player2B.ToString();
            this.config["player2TurboA"] = keyBindings.Player2TurboA.ToString();
            this.config["player2TurboB"] = keyBindings.Player2TurboB.ToString();
            this.config["loadState"] = keyBindings.LoadState.ToString();
            this.config["saveState"] = keyBindings.SaveState.ToString();
            this.config["rewind"] = keyBindings.Rewind.ToString();
            this.config["fastForward"] = keyBindings.FastForward.ToString();
            this.config["pause"] = keyBindings.Pause.ToString();
            this.config["power"] = keyBindings.Power.ToString();
            this.config["restart"] = keyBindings.Reset.ToString();
        }
        #endregion

        #region debug_tools
        private void nameTablesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!this.generateNameTables)
            {
                this.nameTablePreview = new NameTablePreview(cpu == null ? this.colorChart : cpu.PPU.colorChart, this.generateLine);
                this.nameTablePreview.FormClosed += new FormClosedEventHandler(this.nameTablePreviewForm_Closed);
                this.generateNameTables = true;
                nameTablePreview.Show();
            }
            else
                this.nameTablePreview.Activate();
        }

        private void nameTablePreviewForm_Closed(object sender, FormClosedEventArgs e)
        {
            this.generateNameTables = false;
        }

        private void patternTablesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!this.generatePatternTables)
            {
                this.patternTablePreview = new PatternTablePreview(cpu == null ? this.colorChart : cpu.PPU.colorChart, this.generatePatternLine);
                this.patternTablePreview.FormClosed += new FormClosedEventHandler(this.patternTablePreviewForm_Closed);
                this.generatePatternTables = true;
                patternTablePreview.Show();
            }
            else
                this.patternTablePreview.Activate();
        }

        private void patternTablePreviewForm_Closed(object sender, FormClosedEventArgs e)
        {
            this.generatePatternTables = false;
        }

        private void memoryViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.memoryViewer = new MemoryViewer();
            this.memoryViewer.Show();
            if(cpu != null)
                memoryViewer.updateMemory(cpu.Memory, 0x10000);
            this.memoryViewer.Text = "Memory Viewer";

        }
        private void pPUMemoryViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.memoryViewer = new MemoryViewer();
            this.memoryViewer.Show();
            if (cpu != null)
                memoryViewer.updateMemory(cpu.PPU.PPUMemory, 0x4000);
            this.memoryViewer.Text = "PPU Memory Viewer";

        }

        private void debuggerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cpu != null)
                debugger.Show();
        }

        private void cheatFinderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cpu != null)
            {
                CheatFinder cheatFinder = new CheatFinder(cpu.debug);
                cheatFinder.Show();
            }
        }

        private void ShowLog()
        {
            if (this.cpu != null)
            {
                File.WriteAllText("log.txt", this.cpu.debug.logBuilder.ToString());
                Process log = new Process();
                log.StartInfo.FileName = this.config["logReader"];
                log.StartInfo.Arguments = "log.txt";
                log.Start();
            }
        }

        private void enableLoggingToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.cpu.debug.logging = !this.cpu.debug.logging;
            enableLoggingToolStripMenuItem.Checked = this.cpu.debug.logging;
        }

        private void openLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowLog();
        }

        private void testConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Console con = new Console();
            con.Show();
        }

        private void openWithFCEUXToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process FCEXU = new Process();
            FCEXU.StartInfo.FileName = this.config["previewEmu"];
            FCEXU.StartInfo.Arguments = "\"" + this.romPath + "\"";
            FCEXU.Start();
        }
        #endregion

        void DipDiag_FormClosing(object sender, FormClosingEventArgs e)
        {
            this.cpu.dip1 = ((DipDialog)sender).dip1.Checked;
            this.cpu.dip2 = ((DipDialog)sender).dip2.Checked;
            this.cpu.dip3 = ((DipDialog)sender).dip3.Checked;
            this.cpu.dip4 = ((DipDialog)sender).dip4.Checked;
            this.cpu.dip5 = ((DipDialog)sender).dip5.Checked;
            this.cpu.dip6 = ((DipDialog)sender).dip6.Checked;
            this.cpu.dip7 = ((DipDialog)sender).dip7.Checked;
            this.cpu.dip8 = ((DipDialog)sender).dip8.Checked;
            this.state = SystemState.Playing;

        }


        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (debugger != null)
                debugger.Close();
            cpu = null;
            this.SaveGame();
            this.Text = "Emu-o-Tron";
            this.romPath = "";
            state = SystemState.Empty;
            surfaceControl.Visible = false;
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void Program_FormClosing(object sender, FormClosingEventArgs e)
        {
            this.SaveGame();
            this.SaveKeys();
            this.config.Save();
            if (Directory.Exists(this.config["tmpDir"]))
            {
                string[] tmpFiles = Directory.GetFiles(this.config["tmpDir"]);
                for (int i = 0; i < tmpFiles.Length; i++)
                {
                    if (Path.GetFileName(tmpFiles[i]).IndexOf(".tmp") != -1)
                        File.Delete(tmpFiles[i]);
                }
            }
            if (netPlay)
            {
                netClient.Close();
                if (netPlayServer)
                {
                    netServer.Close();
                }
            }
            if (renderer != null)
                renderer.Destroy();
            if (audio != null)
                audio.Destroy();
            if (input != null)
                input.Destroy();
            Closed = true;
        }

        private void romInfoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.cpu != null)
            {
                RomInfoBox romInfoBox = new RomInfoBox(cpu.debug.romInfo.ToString());
                romInfoBox.Show();
            }
            else
            {
                RomInfoBox romInfoBox = new RomInfoBox("");
                romInfoBox.Show();
            }
        }

        private void helpToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            Help.ShowHelp(this.ActiveControl, this.config["helpFile"]);
        }

        private void recordWAVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (state != SystemState.Empty)
            {
                recordDialog.FileName = cpu.rom.fileName;
                SystemState old = state;
                state = SystemState.SystemPause;
                recordDialog.DefaultExt = ".wav";
                recordDialog.Title = "Save WAV";
                recordDialog.Filter = "WAV Files|*.wav";
                if (recordDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    wavRecorder = new WAVOutput(recordDialog.FileName, cpu.APU.sampleRate);
                    stopWAVToolStripMenuItem.Enabled = true;
                }
                state = old;
            }
        }

        private void recordAVSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (state != SystemState.Empty)
            {
                recordDialog.FileName = cpu.rom.fileName;
                SystemState old = state;
                state = SystemState.SystemPause;
                recordDialog.DefaultExt = ".avs";
                recordDialog.Title = "Save AVS";
                recordDialog.Filter = "AVS Files|*.avs";
                if (recordDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    avsRecorder = new AVSOutput(recordDialog.FileName, 256, 240, cpu.APU.FPS, cpu.APU.sampleRate);
                    stopAVSToolStripMenuItem.Enabled = true;
                }
                state = old;
            }

        }

        private void stopWAVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            stopWAVToolStripMenuItem.Enabled = false;
            wavRecorder.CompleteRecording();
        }

        private void stopAVSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            stopAVSToolStripMenuItem.Enabled = false;
            avsRecorder.CompleteRecording();
        }

        private void enableSoundToolStripMenuItem_Click(object sender, EventArgs e)
        {
            enableSoundToolStripMenuItem.Checked = !enableSoundToolStripMenuItem.Checked;
            if (cpu != null)
                cpu.APU.mute = !enableSoundToolStripMenuItem.Checked;
            config["sound"] = enableSoundToolStripMenuItem.Checked ? "1" : "0";
            if (audio != null)
            {
                if (enableSoundToolStripMenuItem.Checked)
                    audio.SetVolume(Convert.ToInt32(config["volume"]) / 100f);
                else
                    audio.SetVolume(0);
            }
        }

        private void soundToolStripMenuItem_Click(object sender, EventArgs e)
        {
            soundConfig = new SoundConfig(volume);
            soundConfig.soundVolume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.pulse1Volume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.pulse2Volume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.triangleVolume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.noiseVolume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.dmcVolume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.externalVolume.ValueChanged += new EventHandler(soundVolume_ValueChanged);
            soundConfig.Show();
        }

        private void soundVolume_ValueChanged(object sender, EventArgs e)
        {
            config["volume"] = soundConfig.soundVolume.Value.ToString();
            if (audio != null)
                audio.SetVolume(Convert.ToInt32(config["volume"]) / 100f);
            volume.master = soundConfig.soundVolume.Value / 100f;
            volume.square1 = soundConfig.pulse1Volume.Value / 100f;
            volume.square2 = soundConfig.pulse2Volume.Value / 100f;
            volume.triangle = soundConfig.triangleVolume.Value / 100f;
            volume.noise = soundConfig.noiseVolume.Value / 100f;
            volume.dmc = soundConfig.dmcVolume.Value / 100f;
            volume.external = soundConfig.externalVolume.Value / 100f;
            if (cpu != null)
                cpu.APU.volume = volume;
        }

        private void showFPSToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            this.showFPS = !this.showFPS;
            if (this.showFPS)
                this.config["showFPS"] = "1";
            else
                this.config["showFPS"] = "0";
        }

        private void spritesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cpu != null)
                cpu.PPU.displaySprites = !cpu.PPU.displaySprites;
            this.spritesToolStripMenuItem.Checked = !this.spritesToolStripMenuItem.Checked;
            config["displaySprites"] = this.spritesToolStripMenuItem.Checked ? "1" : "0";
        }

        private void backgroundToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cpu != null)
                cpu.PPU.displayBG = !cpu.PPU.displayBG;
            this.backgroundToolStripMenuItem.Checked = !this.backgroundToolStripMenuItem.Checked;
            config["displayBG"] = this.backgroundToolStripMenuItem.Checked ? "1" : "0";
        }

        private void spriteLimitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cpu != null)
                cpu.PPU.enforceSpriteLimit = !cpu.PPU.enforceSpriteLimit;
            this.spriteLimitToolStripMenuItem.Checked = !this.spriteLimitToolStripMenuItem.Checked;
            config["disableSpriteLimit"] = this.spriteLimitToolStripMenuItem.Checked ? "1" : "0";
        }

        private void showInputToolStripMenuItem_Click(object sender, EventArgs e)
        {
            showInput = !showInput;
            showInputToolStripMenuItem.Checked = showInput;
            config["showInput"] = showInput ? "1" : "0";
        }

        private void smoothOutputToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.config["smoothOutput"] = smoothOutputToolStripMenuItem.Checked ? "1" : "0";
            if (renderer != null)
            {
                renderer.SmoothOutput(smoothOutputToolStripMenuItem.Checked);
                ResetVideo = true;
            }
        }

        private void nTSCToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in regionToolStripMenuItem.DropDownItems)
                item.Checked = false;
            nTSCToolStripMenuItem.Checked = true;
            config["region"] = ((int)SystemType.NTSC).ToString();
            if (state != SystemState.Empty)
            {
                this.SaveGame();
                this.StartEmu();
            }
        }

        private void pALToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in regionToolStripMenuItem.DropDownItems)
                item.Checked = false;
            pALToolStripMenuItem.Checked = true;
            config["region"] = ((int)SystemType.PAL).ToString();
            if (state != SystemState.Empty)
            {
                this.SaveGame();
                this.StartEmu();
            }
        }

        private void ToggleFullScreen()
        {
            if (fullScreen)
            {
                this.fullScreen = false;
                this.menuStrip.Show();
                this.Size = this.smallSize;
                this.Location = this.smallLocation;
            }
            else
            {
                this.fullScreen = true;
                this.smallLocation = this.Location;
                this.smallSize = this.Size;
                this.menuStrip.Hide();
            }
            PrepareScaler();
        }

        private void Program_Resize(object sender, EventArgs e)
        {
            if (imageScaler != null)
            {
                PrepareScaler();
                this.config["width"] = this.Width.ToString();
                this.config["height"] = this.Height.ToString();
            }
        }

        #region scalers
        private void sizeableToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            sizeableToolStripMenuItem.Checked = true;
            imageScaler = new Sizeable();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "sizeable";
        }

        private void fillToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            fillToolStripMenuItem.Checked = true;
            imageScaler = new Fill();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "fill";
        }

        private void xToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            xToolStripMenuItem.Checked = true;
            imageScaler = new NearestNeighbor1x();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "1x";
        }

        private void xToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            xToolStripMenuItem1.Checked = true;
            imageScaler = new NearestNeighbor2x();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "2x";
        }

        private void scale2xToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            scale2xToolStripMenuItem.Checked = true;
            imageScaler = new Scale2x();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "scale2x";
        }

        private void scale3xToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            scale3xToolStripMenuItem.Checked = true;
            imageScaler = new Scale3x();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "scale3x";
        }

        private void tVAspectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            tVAspectToolStripMenuItem.Checked = true;
            imageScaler = new TVAspect();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "tv";
        }

        private void hQ2xToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ToolStripMenuItem item in videoModeToolStripMenuItem.DropDownItems)
                item.Checked = false;
            hQ2xToolStripMenuItem.Checked = true;
            imageScaler = new HQ2x();
            if (renderer != null)
                renderer.ChangeScaler(imageScaler);
            PrepareScaler();
            config["scaler"] = "hq2x";
        }

        private void PrepareScaler()
        {
            int oldWidth = surfaceControl.Width;
            int oldHeight = surfaceControl.Height;
            if (fullScreen)
            {
                this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                this.Location = new Point(0, 0);
                this.Size = SystemInformation.PrimaryMonitorSize;
                if (this.imageScaler.MaintainAspectRatio)
                {
                    int height = this.Height;
                    int width = this.Width;
                    if (height / imageScaler.RatioY > width / imageScaler.RatioX)
                    {
                        this.surfaceControl.Width = width;
                        this.surfaceControl.Height = (int)(width * (imageScaler.RatioY / imageScaler.RatioX));
                        this.surfaceControl.Location = new Point(0, (int)(((height - (width * (imageScaler.RatioY / imageScaler.RatioX))) / 2.0)));
                    }
                    else
                    {
                        this.surfaceControl.Height = height;
                        this.surfaceControl.Width = (int)(height * (imageScaler.RatioX / imageScaler.RatioY));
                        this.surfaceControl.Location = new Point((int)((width - (height * (imageScaler.RatioX / imageScaler.RatioY))) / 2.0), 0);
                    }
                }
                else
                {
                    this.surfaceControl.Width = this.Width;
                    this.surfaceControl.Height = this.Height;
                    this.surfaceControl.Location = new Point(0, 0);
                }
            }
            else if (imageScaler.MaintainAspectRatio)
            {
                if (imageScaler.IsResizable)
                {
                    this.FormBorderStyle = FormBorderStyle.Sizable;
                    this.MaximizeBox = true;
                }
                else
                {
                    this.FormBorderStyle = FormBorderStyle.Fixed3D;
                    this.MaximizeBox = false;
                    int borderWidth = (Width - ClientSize.Width);
                    int titlebarHeight = (Height - ClientSize.Height);
                    this.Width = borderWidth + this.imageScaler.ResizedX;
                    this.Height = titlebarHeight + this.menuStrip.Height + this.imageScaler.ResizedY;
                }
                int height = ClientSize.Height - this.menuStrip.Height;
                int width = ClientSize.Width;
                if (height / imageScaler.RatioY > width / imageScaler.RatioX)
                {
                    this.surfaceControl.Width = width;
                    this.surfaceControl.Height = (int)(width * (imageScaler.RatioY / imageScaler.RatioX));
                    this.surfaceControl.Location = new Point(0, (int)(((height - (width * (imageScaler.RatioY / imageScaler.RatioX))) / 2.0)) + this.menuStrip.Height);
                }
                else
                {
                    this.surfaceControl.Height = height;
                    this.surfaceControl.Width = (int)(height * (imageScaler.RatioX / imageScaler.RatioY));
                    this.surfaceControl.Location = new Point((int)((width - (height * (imageScaler.RatioX / imageScaler.RatioY))) / 2.0), this.menuStrip.Height);
                }
            }
            else
            {
                if (imageScaler.IsResizable)
                {
                    this.FormBorderStyle = FormBorderStyle.Sizable;
                    this.MaximizeBox = true;
                }
                else
                {
                    this.FormBorderStyle = FormBorderStyle.Fixed3D;
                    this.MaximizeBox = false;
                    int borderWidth = (Width - ClientSize.Width);
                    int titlebarHeight = (Height - ClientSize.Height);
                    this.Width = borderWidth + this.imageScaler.ResizedX;
                    this.Height = titlebarHeight + this.menuStrip.Height + this.imageScaler.ResizedY;
                }
                this.surfaceControl.Width = ClientSize.Width;
                this.surfaceControl.Height = ClientSize.Height - this.menuStrip.Height;
                this.surfaceControl.Location = new Point(0, this.menuStrip.Height);
            }
            if (oldWidth != surfaceControl.Width || oldHeight != surfaceControl.Height)
            {
                if (renderer != null)
                    ResetVideo = true;
            }
        }
        #endregion

        #region netplay
        private void startGameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (netPlay)
                netClient.Close();
            if (netPlayServer)
                netServer.Close();
            NetPlayConnect NPC = new NetPlayConnect("127.0.0.1");
            if (NPC.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                netServer = new NetPlayServer(Convert.ToInt32(config["serverPort"]));
                netClient = new NetPlayClient(NPC.ip, Convert.ToInt32(config["serverPort"]), NPC.nick);
                Thread.Sleep(100);
                netPlay = true;
                netPlayServer = true;
            }
        }

        private void joinGameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (netPlay)
                netClient.Close();
            if (netPlayServer)
                netServer.Close();
            NetPlayConnect NPC = new NetPlayConnect();
            if (NPC.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                netClient = new NetPlayClient(NPC.ip, Convert.ToInt32(config["serverPort"]), NPC.nick);
                Thread.Sleep(100);
                netPlay = true;
            }
        }
        public static byte PlayerToByte(EmuoTron.Controller player)
        {
            byte input = 0;
            if (player.a)
                input |= 1;
            input <<= 1;
            if (player.b)
                input |= 1;
            input <<= 1;
            if (player.start)
                input |= 1;
            input <<= 1;
            if (player.select)
                input |= 1;
            input <<= 1;
            if (player.left)
                input |= 1;
            input <<= 1;
            if (player.right)
                input |= 1;
            input <<= 1;
            if (player.up)
                input |= 1;
            input <<= 1;
            if (player.down)
                input |= 1;
            return input;
        }
        public static EmuoTron.Controller ByteToPlayer(byte input)
        {
            EmuoTron.Controller player = new EmuoTron.Controller();
            player.down = ((input & 1) == 1);
            input >>= 1;
            player.up = ((input & 1) == 1);
            input >>= 1;
            player.right = ((input & 1) == 1);
            input >>= 1;
            player.left = ((input & 1) == 1);
            input >>= 1;
            player.select = ((input & 1) == 1);
            input >>= 1;
            player.start = ((input & 1) == 1);
            input >>= 1;
            player.b = ((input & 1) == 1);
            input >>= 1;
            player.a = ((input & 1) == 1);
            return player;
        }
#endregion

        #region FDS
        void DiskSide_Click(object sender, EventArgs e)
        {
            if (!cpu.GetEjectDisk())
            {
                cpu.SetDiskSide((int)((ToolStripMenuItem)sender).Tag);
                ejectDiskToolStripMenuItem.DropDownItems.Clear();
                ejectDiskToolStripMenuItem.Text = "Eject Disk";
                cpu.EjectDisk(true);
            }
            else
                MessageBox.Show("Must eject the disk prior to switching sides.");
        }

        private void ejectDiskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (cpu != null)
            {
                if (cpu.GetEjectDisk())
                {
                    ejectDiskToolStripMenuItem.DropDownItems.Clear();
                    cpu.EjectDisk(false);
                    ejectDiskToolStripMenuItem.Text = "Insert Disk";
                    if (cpu.GetSideCount() != 0)
                    {
                        ToolStripMenuItem[] diskSides = new ToolStripMenuItem[cpu.GetSideCount()];
                        for (int i = 0; i < cpu.GetSideCount(); i++)
                        {
                            diskSides[i] = new ToolStripMenuItem("Disk " + ((i / 2) + 1).ToString() + " Side " + (i % 2 == 0 ? "A" : "B"));
                            diskSides[i].Tag = (i);
                            diskSides[i].Click += new EventHandler(DiskSide_Click);
                        }
                        ejectDiskToolStripMenuItem.DropDownItems.AddRange(diskSides);
                    }
                }
            }
        }
        #endregion

        private void memoryVisualizerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (memoryVis == null || memoryVis.Visible == false)
            {
                memoryVis = new MemoryVis();
                memoryVis.Show();
            }
            else
                memoryVis.BringToFront();
        }

        private void soundVisualizerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (soundVis == null || soundVis.Visible == false)
            {
                soundVis = new SoundVis();
                soundVis.Show();
            }
            else
                soundVis.BringToFront();
        }

    }
}
