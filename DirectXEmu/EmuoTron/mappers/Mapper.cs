﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace EmuoTron.mappers
{
    public abstract class Mapper
    {
        protected NESCore nes;
        public bool cycleIRQ;
        public virtual bool interruptMapper
        {
            get;
            set;
        }
        public virtual void Init() { }
        public virtual byte Read(byte value, ushort address) { return value; }
        public virtual void Write(byte value, ushort address) { }
        public virtual void IRQ(int scanline, int vblank) { }
        public virtual void StateSave(BinaryWriter writer) { }
        public virtual void StateLoad(BinaryReader reader) { }
    }
}
