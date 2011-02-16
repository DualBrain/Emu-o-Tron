﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using EmuoTron;

namespace DirectXEmu
{
    class EmuConfig
    {
        private string configFile;
        private Dictionary<string, string> settings;
        public Dictionary<string, string> defaults;
        public EmuConfig(string path)
        {
            this.configFile = path;
            if (!File.Exists(this.configFile))
            {
                FileStream tmpFile = File.Create(this.configFile);
                tmpFile.Close();
            }
            this.defaults = this.LoadDefaults();
            this.settings = this.Load(this.configFile);
        }
        private Dictionary<string, string> LoadDefaults()
        {
            Dictionary<string, string> defaults = new Dictionary<string, string>();
            defaults["palette"] = @"palettes\Nestopia.pal";
            defaults["paletteDir"] = @"palettes";
            defaults["movieDir"] = @"movies";
            defaults["sramDir"] = @"sav";
            defaults["savestateDir"] = @"savestates";
            defaults["romPath1"] = @"roms";
            defaults["romPath2"] = "";
            defaults["romPath3"] = "";
            defaults["romPath4"] = "";
            defaults["romPath5"] = "";
            defaults["recentFile1"] = "";
            defaults["recentFile2"] = "";
            defaults["recentFile3"] = "";
            defaults["recentFile4"] = "";
            defaults["recentFile5"] = "";
            defaults["logReader"] = "";
            defaults["previewEmu"] = "";
            defaults["showFPS"] = "0";
            defaults["showInput"] = "0";
            defaults["helpFile"] = @"Emu-o-Tron.chm";
            defaults["player1Up"] = "UpArrow";
            defaults["player1Down"] = "DownArrow";
            defaults["player1Left"] = "LeftArrow";
            defaults["player1Right"] = "RightArrow";
            defaults["player1Start"] = "Return";
            defaults["player1Select"] = "RightShift";
            defaults["player1A"] = "Z";
            defaults["player1B"] = "X";
            defaults["player1TurboA"] = "A";
            defaults["player1TurboB"] = "S";
            defaults["player2Up"] = "NumberPad8";
            defaults["player2Down"] = "NumberPad5";
            defaults["player2Left"] = "NumberPad4";
            defaults["player2Right"] = "NumberPad6";
            defaults["player2Start"] = "NumberPad7";
            defaults["player2Select"] = "NumberPad9";
            defaults["player2A"] = "NumberPad1";
            defaults["player2B"] = "NumberPad3";
            defaults["player2TurboA"] = "Home";
            defaults["player2TurboB"] = "End";
            defaults["fastForward"] = "LeftShift";
            defaults["rewind"] = "Tab";
            defaults["saveState"] = "D1";
            defaults["loadState"] = "D2";
            defaults["pause"] = "Space";
            defaults["restart"] = "Back";
            defaults["power"] = "Delete";
            defaults["scaler"] = "sizeable";
            defaults["width"] = "528";
            defaults["height"] = "542";
            defaults["rewindEnabled"] = "1";
            defaults["rewindBufferFreq"] = "2";
            defaults["rewindBufferSeconds"] = "30";
            defaults["7z"] = @"7z.dll";
            defaults["7z64"] = @"7z64.dll";
            defaults["tmpDir"] = @"tmp";
            defaults["disableSpriteLimit"] = "1";
            defaults["displayBG"] = "1";
            defaults["displaySprites"] = "1";
            defaults["sound"] = "1";
            defaults["volume"] = "100";
            defaults["showDebug"] = "0";
            defaults["region"] = ((int)SystemType.NTSC).ToString();
            defaults["serverPort"] = "7878";
            defaults["fdsBios"] = @"disksys.rom";
            defaults["sampleRate"] = "48000";
            defaults["portOne"] = "Controller";
            defaults["portTwo"] = "Controller";
            defaults["fourScore"] = "0";
            defaults["smoothOutput"] = "0";
#if DEBUG
            defaults["romPath1"] = @"C:\Games\Emulators\Roms\NES";
            defaults["romPath2"] = @"C:\Games\Emulators\Roms\NES";
            defaults["romPath3"] = @"C:\Games\Emulators\Roms\MapperNes";
            defaults["romPath4"] = @"C:\Games\Emulators\Roms\TestNes";
            defaults["romPath5"] = "";
            defaults["logReader"] = @"C:\Program Files\Vim\vim72\gvim.exe";
            defaults["previewEmu"] = @"C:\Games\Emulators\FCEUX-2.1.1\fceux.exe";
            defaults["showDebug"] = "1";
#endif

            return defaults;
        }
        private Dictionary<string, string> Load(string path)
        {
            Dictionary<string, string> settings = new Dictionary<string, string>();
            StreamReader conf = File.OpenText(path);
            while (!conf.EndOfStream)
            {
                string line = conf.ReadLine();
                settings[line.Substring(0, line.IndexOf(' '))] =  line.Substring(line.IndexOf(' ') + 1).Trim();
            }
            conf.Close();
            return settings;
        }
        public void Save()
        {
            StringBuilder conf = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in this.settings)
            {
                conf.AppendLine(entry.Key + " " + entry.Value);
            }
            File.WriteAllText(this.configFile, conf.ToString());
        }
        public string Get(string key)
        {
            string val;
            if (this.settings.TryGetValue(key, out val))
            {
                return val;
            }
            else
            {
                if (this.defaults.TryGetValue(key, out val))
                {
                    this.settings[key] = val;
                    return val;
                }
                else
                    throw (new Exception("Invalid setting."));
            }
        }
        public void Set(string key, string val)
        {
            this.settings[key] = val;
        }
        public string this[string key]
        {
            get
            {
                return this.Get(key);
            }
            set
            {
                this.Set(key, value);
            }
        }
    }
}
