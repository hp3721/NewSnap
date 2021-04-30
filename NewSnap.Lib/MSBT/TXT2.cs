﻿using System;
using System.Collections.Generic;

namespace Lib
{
    public class TXT2 : MSBTSection
    {
        public uint NumberOfStrings;

        public readonly List<MSBTTextString> Strings = new();

        public TXT2() : base(string.Empty, Array.Empty<byte>())
        {
        }
    }
}
