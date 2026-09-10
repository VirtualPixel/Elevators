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
        public static ConfigEntry<bool> VoidRescue = null!;
        public static ConfigEntry<bool> ShowLiftHint = null!;
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

            ExtraLoot = config.Bind("Floors", "ExtraLoot", 50, new ConfigDescription(
                "Valuables on the new floors, as a percent of what the ground floor gets. The ground floor keeps its normal amount, " +
                "these are on top, and the quota grows with them like any other valuable.",
                new AcceptableValueRange<int>(0, 150)));

            EnemiesOnNewFloors = config.Bind("Floors", "EnemiesOnNewFloors", true,
                "Monsters can turn up on the new floors. The game already moves a monster that has lost you to a fresh spot some way off, and with this on those spots include the floor you rode to, so a ride buys time, not safety. The elevator is loud: leaving and arriving draws anything nearby to the shaft. Off keeps them to the ground floor.");

            AvoidRooms = config.Bind("Floors", "AvoidRooms", "Bottomless,Outside",
                "Comma separated. A room whose name contains one of these is never picked for a new floor. " +
                "The defaults are rooms that open to the sky or drop out of the world.");

            LiftCooldown = config.Bind("Lift", "LiftCooldown", 4f, new ConfigDescription(
                "Seconds before the same player can ride the lift again.",
                new AcceptableValueRange<float>(1f, 15f)));

            ShowLiftHint = config.Bind("Lift", "ShowLiftHint", true,
                "A line at the bottom of the HUD with the distance and direction to the nearest lift pad. Only the host sees it.");

            VoidRescue = config.Bind("Lift", "VoidRescue", true,
                "A player who spends a couple of seconds outside every room, on a roof between floors say, gets lifted back to the ground pad.");

            LoggingLevel = config.Bind("General", "LogLevel", VerbosityLevel.Off,
                "Off: key events only. Debug: floor plans, lift pads and rides. Verbose: every step.");
        }
    }
}
