using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Upstairs.Services
{
    // What the host tells every client about this level's floors and lift pads, packed into one
    // room property so late joiners get it too. Pure string work, no Unity.
    internal sealed class LiftPayload
    {
        public sealed class Pad
        {
            public string Name = "";
            public float X, Y, Z, Yaw;
        }

        public int Nonce;
        public string Level = "";
        public float GroundTop;
        public float GroundBottom;
        public bool HasUp;
        public bool HasDown;
        public List<Pad> Pads { get; } = new List<Pad>();

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string Pack()
        {
            var sb = new StringBuilder();
            sb.Append(Nonce).Append('|').Append(Level.Replace('|', ' ')).Append('|')
              .Append(GroundTop.ToString("0.##", Inv)).Append('|').Append(GroundBottom.ToString("0.##", Inv)).Append('|')
              .Append(HasUp ? '1' : '0').Append(HasDown ? '1' : '0');
            foreach (Pad pad in Pads)
            {
                sb.Append('|').Append(pad.Name).Append(':')
                  .Append(pad.X.ToString("0.##", Inv)).Append(',').Append(pad.Y.ToString("0.##", Inv)).Append(',')
                  .Append(pad.Z.ToString("0.##", Inv)).Append(',').Append(pad.Yaw.ToString("0.#", Inv));
            }
            return sb.ToString();
        }

        public static LiftPayload? Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            string[] parts = text!.Split('|');
            if (parts.Length < 5)
                return null;
            var p = new LiftPayload();
            if (!int.TryParse(parts[0], NumberStyles.Integer, Inv, out p.Nonce)) return null;
            p.Level = parts[1];
            if (!float.TryParse(parts[2], NumberStyles.Float, Inv, out p.GroundTop)) return null;
            if (!float.TryParse(parts[3], NumberStyles.Float, Inv, out p.GroundBottom)) return null;
            if (parts[4].Length != 2) return null;
            p.HasUp = parts[4][0] == '1';
            p.HasDown = parts[4][1] == '1';
            for (int i = 5; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon <= 0) return null;
                string[] nums = parts[i].Substring(colon + 1).Split(',');
                if (nums.Length != 4) return null;
                var pad = new Pad { Name = parts[i].Substring(0, colon) };
                if (!float.TryParse(nums[0], NumberStyles.Float, Inv, out pad.X)) return null;
                if (!float.TryParse(nums[1], NumberStyles.Float, Inv, out pad.Y)) return null;
                if (!float.TryParse(nums[2], NumberStyles.Float, Inv, out pad.Z)) return null;
                if (!float.TryParse(nums[3], NumberStyles.Float, Inv, out pad.Yaw)) return null;
                p.Pads.Add(pad);
            }
            return p;
        }
    }
}
