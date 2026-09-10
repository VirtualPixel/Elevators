using System;
using System.Collections.Generic;

namespace Elevators.Services
{
    // The AvoidRooms setting: comma separated fragments, matched case-insensitively against room names.
    internal static class RoomFilter
    {
        public static List<string> Parse(string? setting)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(setting))
                return parts;
            foreach (string raw in setting!.Split(','))
            {
                string part = raw.Trim();
                if (part.Length > 0)
                    parts.Add(part);
            }
            return parts;
        }

        public static bool Avoids(string roomName, List<string> fragments)
        {
            foreach (string fragment in fragments)
            {
                if (roomName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
