using BepInEx.Configuration;

namespace Upstairs.Configuration
{
    public enum VerbosityLevel
    {
        Off = 0,
        Debug = 1,
        Verbose = 2
    }

    internal static class PluginConfig
    {
        public static ConfigEntry<bool> Enabled = null!;
        public static ConfigEntry<bool> UpperFloor = null!;
        public static ConfigEntry<bool> Basement = null!;
        public static ConfigEntry<int> FloorSize = null!;
        public static ConfigEntry<int> ExtraLoot = null!;
        public static ConfigEntry<bool> EnemiesOnNewFloors = null!;
        public static ConfigEntry<string> AvoidRooms = null!;
        public static ConfigEntry<float> LiftCooldown = null!;
        public static ConfigEntry<bool> Announce = null!;
        public static ConfigEntry<VerbosityLevel> LoggingLevel = null!;

        public static void Init(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Host only. Every level gets another floor built out of that level's own rooms, reached by a lift. " +
                "Nobody else in the lobby needs the mod.");

            UpperFloor = config.Bind("Floors", "UpperFloor", true,
                "Build a floor above the level. Stand on the lift to ride up.");

            Basement = config.Bind("Floors", "Basement", false,
                "Build a floor below the level as well. Crouch on the lift to ride down.");

            FloorSize = config.Bind("Floors", "FloorSize", 60, new ConfigDescription(
                "Rooms on each new floor, as a percent of the rooms on the ground floor.",
                new AcceptableValueRange<int>(20, 100)));

            ExtraLoot = config.Bind("Floors", "ExtraLoot", 40, new ConfigDescription(
                "Extra valuables placed on the new floors, as a percent of the valuables the level would normally have. " +
                "The game already spreads its normal valuables over every floor; this is on top. The quota grows with them like any other valuable.",
                new AcceptableValueRange<int>(0, 150)));

            EnemiesOnNewFloors = config.Bind("Floors", "EnemiesOnNewFloors", false,
                "Let monsters spawn and roam on the new floors. Off keeps them on the ground floor, so a ride up is a real escape.");

            AvoidRooms = config.Bind("Floors", "AvoidRooms", "Bottomless,Outside",
                "Comma separated. A room whose name contains one of these is never picked for a new floor. " +
                "The defaults are rooms that open to the sky or drop out of the world.");

            LiftCooldown = config.Bind("Lift", "LiftCooldown", 4f, new ConfigDescription(
                "Seconds before the same player can ride the lift again.",
                new AcceptableValueRange<float>(1f, 15f)));

            Announce = config.Bind("Lift", "Announce", true,
                "Call out the new floor and where the lift is once the level loads, using the game's moon-phase popup. " +
                "Only the host sees it, since only the host runs the mod.");

            LoggingLevel = config.Bind("General", "LogLevel", VerbosityLevel.Off,
                "Off: key events only. Debug: floor plans, lift pads and rides. Verbose: every step.");
        }
    }
}
