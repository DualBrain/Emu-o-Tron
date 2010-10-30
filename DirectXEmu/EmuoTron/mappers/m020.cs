﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace EmuoTron.mappers
{
    class m020 : Mapper
    {
        public uint crc;
        public int sideCount;
        public int currentSide;
        private byte[,] diskData;
        private int diskPointer;
        private bool irqEnable;
        private int irqReload;
        private int irqCounter;
        private bool dataIRQ;
        private bool timerIRQ;
        private bool soundControl;
        private bool dataControl;
        public bool diskInserted = true;
        private bool readWrite;
        private bool dataIRQTrigger;
        private bool driveMotor;
        private int diskOperationTime = 152; //96.4khz / 60 fps / 29780 cycles / frame * 8 bits = 152
        private int diskOperationCounter;

        public override bool interruptMapper
        {
            get
            {
                return dataIRQ || timerIRQ;
            }
        }
        public m020(NESCore nes, Stream diskStream, bool ignoreFileCheck)
        {
            this.nes = nes;
            diskStream.Position = 0x0;
            if (!ignoreFileCheck)
            {
                if (diskStream.ReadByte() != 'F' || diskStream.ReadByte() != 'D' || diskStream.ReadByte() != 'S' || diskStream.ReadByte() != 0x1A)
                {
                    diskStream.Close();
                    throw (new Exception("Invalid File"));
                }
            }
            diskStream.Position = 0x4;
            sideCount = diskStream.ReadByte();
            diskStream.Position = 0x10;
            diskData = new byte[sideCount, 65500];
            crc = 0xFFFFFFFF;
            for (int side = 0; side < sideCount; side++)
            {
                for (int i = 0; i < 65500; i++)
                {
                    byte nextByte = (byte)diskStream.ReadByte();
                    diskData[side, i] = nextByte;
                    crc = CRC32.crc32_adjust(crc, nextByte);
                }
            }
            nes.debug.LogInfo("Disk Sides: " + sideCount.ToString());
            crc = crc ^ 0xFFFFFFFF;
        }
        public override void Init()
        {
            nes.Memory.Swap8kROM(0xE000, 0);
            nes.Memory.Swap8kRAM(0x8000, 1);
            nes.Memory.Swap8kRAM(0xA000, 2);
            nes.Memory.Swap8kRAM(0xC000, 3);
            nes.Memory.SetReadOnly(0x6000, 8, false);
            nes.PPU.PPUMemory.Swap8kRAM(0, 0);
            diskOperationCounter = diskOperationTime;
        }
        public override void Write(byte value, ushort address)
        {
            if((address & 0xFF00) == 0x4000)
            {
                switch (address)
                {
                    case 0x4020:
                        irqReload = (irqReload & 0xFF00) | value;
                        timerIRQ = false;
                        break;
                    case 0x4021:
                        irqReload = (irqReload & 0x00FF) | (value << 8);
                        timerIRQ = false;
                        break;
                    case 0x4022:
                        irqEnable = ((value & 2) != 0);
                        timerIRQ = false;
                        irqCounter = irqReload;
                        break;
                    case 0x4023:
                        dataControl = ((value & 1) != 0);
                        soundControl = ((value & 2) != 0);
                        break;
                    case 0x4024:
                        if (diskInserted && dataControl && !readWrite)
                        {
                            if ((diskPointer >= 0) && (diskPointer < 65000))
                            {
                                diskData[currentSide, diskPointer] = value;
                                dataIRQ = false;
                                diskOperationCounter = diskOperationTime;
                                if (diskPointer < 64999)
                                    diskPointer++;
                            }
                        }
                        break;
                    case 0x4025:
                        driveMotor = ((value & 1) != 0);
                        readWrite = ((value & 4) != 0);
                        if ((value & 0x40) == 0)//http://nesdev.parodius.com/bbs/viewtopic.php?t=738&highlight=fds .fds files do not contain crc bytes so have to jump pointer back
                        {
                            diskPointer -= 2;
                            if (diskPointer < 0)
                                diskPointer = 0;
                        }
                        if (diskPointer < 0)
                            diskPointer = 0;
                        if ((value & 8) != 0)
                            nes.PPU.PPUMemory.HorizontalMirroring();
                        else
                            nes.PPU.PPUMemory.VerticalMirroring();
                        dataIRQTrigger = ((value & 0x80) != 0);
                        if ((value & 0x02) != 0)
                        {
                            diskPointer = 0;
                            diskOperationCounter = diskOperationTime;
                        }
                        break;
                    case 0x4026:
                        break;
                }
            }
        }
        public override byte Read(byte value, ushort address)
        {
            if ((address & 0xFF00) == 0x4000)
            {
                switch (address)
                {
                    case 0x4030:
                        value = 0;
                        if (timerIRQ)
                            value |= 1;
                        if (dataIRQ) 
                            value |= 2;
                        if(diskInserted)
                            value |= 0x80; //reable or writable
                        dataIRQ = false;
                        timerIRQ = false;
                        break;
                    case 0x4031:
                        if (diskInserted)
                        {
                            value = diskData[currentSide, diskPointer];
                            dataIRQ = false;
                            diskOperationCounter = diskOperationTime;
                            if (diskPointer < 64999)
                                diskPointer++;
                        }
                        break;
                    case 0x4032:
                        value = 0;
                        if (!diskInserted)
                            value |= 5;
                        if (!diskInserted || !driveMotor)
                            value |= 2;
                        break;
                    case 0x4033:
                        value = 0x80;
                        break;
                }
            }
            return value;
        
        }
        public override void IRQ(int cycles, int vblank)
        {
            if (irqEnable)
            {
                irqCounter -= cycles;
                if (irqCounter <= 0)
                {
                    timerIRQ = true;
                    irqCounter += irqReload;
                }
            }
            if (diskOperationCounter > 0)
                diskOperationCounter -= cycles;
            if (diskOperationCounter <= 0 && readWrite && dataIRQTrigger)
            {
                dataIRQ = true;
            }
        }

        public void EjectDisk(bool diskInserted)
        {
            this.diskInserted = diskInserted;
            diskPointer = 0;
            diskOperationCounter = diskOperationTime;
        }
        public void SetDiskSide(int diskSide)
        {
            this.currentSide = diskSide;
            diskPointer = 0;
            diskOperationCounter = diskOperationTime;
        }
        public override void StateLoad(BinaryReader reader) { }
        public override void StateSave(BinaryWriter writer) { }
    }
}