namespace DBLabs.Services
{
    /// <summary>Auto-assigns a distinguishable color to each new class, cycling through a fixed palette.</summary>
    public static class ClassColorPalette
    {
        private static readonly string[] Colors =
        {
            "#4C8DFF", // blue
            "#3ECF6B", // green
            "#F5A623", // orange
            "#E5484D", // red
            "#22C3D6", // cyan
            "#C86DD7", // purple
            "#E8D34C", // yellow
            "#FF7A9E", // pink
            "#7CE38B", // mint
            "#6E8CFF", // indigo
            "#FF9D5C", // amber
            "#5CE0D8", // teal
        };

        public static string NextColor(int existingCount) => Colors[existingCount % Colors.Length];
    }
}
