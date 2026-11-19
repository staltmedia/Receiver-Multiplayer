using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReceiverMultiplayer.Net
{
    /// <summary>Thrown for any malformed or out-of-bounds network input. The sender is dropped.</summary>
    sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    [StructLayout(LayoutKind.Explicit)]
    struct FloatBits
    {
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public uint U;
    }

    /// <summary>Little-endian binary writer over a reusable buffer. Only fixed, explicit field types.</summary>
    sealed class NetWriter
    {
        byte[] _buf;
        int _len;

        public NetWriter(int capacity = 256) { _buf = new byte[capacity]; }

        public byte[] Buffer => _buf;
        public int Length => _len;
        public void Reset() => _len = 0;

        void Ensure(int n)
        {
            if (_len + n > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        }

        public void U8(byte v) { Ensure(1); _buf[_len++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Ensure(2); _buf[_len++] = (byte)v; _buf[_len++] = (byte)(v >> 8); }
        public void I16(short v) => U16(unchecked((ushort)v));

        public void U32(uint v)
        {
            Ensure(4);
            _buf[_len++] = (byte)v;
            _buf[_len++] = (byte)(v >> 8);
            _buf[_len++] = (byte)(v >> 16);
            _buf[_len++] = (byte)(v >> 24);
        }

        public void I32(int v) => U32(unchecked((uint)v));
        public void F32(float v) => U32(new FloatBits { F = v }.U);

        /// <summary>Append raw bytes (used to relay a validated payload unchanged).</summary>
        public void Raw(byte[] src, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(src, offset, _buf, _len, count);
            _len += count;
        }

        /// <summary>UTF-8 with a u16 byte-length prefix. Over-long input is cut at a character boundary.</summary>
        public void Str(string s, int maxBytes)
        {
            s = s ?? "";
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > maxBytes)
            {
                int cut = maxBytes;
                while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--; // don't split a multi-byte char
                Array.Resize(ref bytes, cut);
            }
            U16((ushort)bytes.Length);
            Ensure(bytes.Length);
            System.Buffer.BlockCopy(bytes, 0, _buf, _len, bytes.Length);
            _len += bytes.Length;
        }
    }

    /// <summary>Bounds-checked reader. Every read validates length; violations throw <see cref="ProtocolException"/>.</summary>
    struct NetReader
    {
        readonly byte[] _buf;
        readonly int _end;
        int _pos;

        public NetReader(byte[] buffer, int offset, int count)
        {
            if (buffer == null || offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ProtocolException("bad buffer");
            _buf = buffer;
            _pos = offset;
            _end = offset + count;
        }

        public int Remaining => _end - _pos;

        /// <summary>Read exactly <paramref name="count"/> raw bytes (the caller bounds the count first).</summary>
        public byte[] Bytes(int count)
        {
            if (count < 0) throw new ProtocolException("negative length");
            Need(count);
            var b = new byte[count];
            System.Buffer.BlockCopy(_buf, _pos, b, 0, count);
            _pos += count;
            return b;
        }

        /// <summary>Append the unread bytes to <paramref name="w"/> without consuming them.</summary>
        public void CopyRestTo(NetWriter w) => w.Raw(_buf, _pos, _end - _pos);

        void Need(int n)
        {
            if (_end - _pos < n) throw new ProtocolException("message truncated");
        }

        public byte U8() { Need(1); return _buf[_pos++]; }

        public bool Bool()
        {
            byte b = U8();
            if (b > 1) throw new ProtocolException("bad bool");
            return b == 1;
        }

        public ushort U16() { Need(2); ushort v = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8)); _pos += 2; return v; }
        public short I16() => unchecked((short)U16());

        public uint U32()
        {
            Need(4);
            uint v = (uint)(_buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24));
            _pos += 4;
            return v;
        }

        public int I32() => unchecked((int)U32());

        public float F32()
        {
            float f = new FloatBits { U = U32() }.F;
            if (float.IsNaN(f) || float.IsInfinity(f)) throw new ProtocolException("bad float");
            return f;
        }

        public string Str(int maxBytes)
        {
            int n = U16();
            if (n > maxBytes) throw new ProtocolException("string too long");
            Need(n);
            string s;
            try
            {
                s = new UTF8Encoding(false, true).GetString(_buf, _pos, n);
            }
            catch (ArgumentException)
            {
                throw new ProtocolException("invalid UTF-8");
            }
            _pos += n;
            return s;
        }

        /// <summary>Call after the last field: trailing bytes mean a malformed or hostile message.</summary>
        public void End()
        {
            if (_pos != _end) throw new ProtocolException("trailing bytes");
        }
    }
}
