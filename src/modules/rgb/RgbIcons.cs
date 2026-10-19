namespace MultiTool.Modules.Rgb;

/// <summary>An icon per kind of device, as path data in 24 × 24 units.</summary>
internal static class RgbIcons
{
    public static string For(RgbKind kind) => kind switch
    {
        RgbKind.Keyboard =>
            "M3 6h18a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1Zm1 2v8h16V8H4Zm2 1h2v2H6V9Zm3 0h2v2H9V9Zm3 0h2v2h-2V9Zm3 0h3v2h-3V9ZM6 12h2v2H6v-2Zm3 0h6v2H9v-2Zm7 0h2v2h-2v-2Z",
        RgbKind.Mouse =>
            "M12 2a6 6 0 0 1 6 6v8a6 6 0 0 1-12 0V8a6 6 0 0 1 6-6Zm0 2a4 4 0 0 0-4 4v8a4 4 0 0 0 8 0V8a4 4 0 0 0-4-4Zm-1 2h2v4h-2V6Z",
        RgbKind.Headset =>
            "M12 3a9 9 0 0 1 9 9v6a3 3 0 0 1-3 3h-2v-8h3v-1a7 7 0 0 0-14 0v1h3v8H6a3 3 0 0 1-3-3v-6a9 9 0 0 1 9-9Z",
        RgbKind.Mousepad =>
            "M3 7h18a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1Zm1 2v6h16V9H4Zm11 1a2 2 0 0 1 2 2v1a2 2 0 0 1-4 0v-1a2 2 0 0 1 2-2Z",
        RgbKind.Controller =>
            "M7 7h10a5 5 0 0 1 4.9 6l-.6 3a3 3 0 0 1-5.1 1.4L14.6 16H9.4l-1.6 1.4A3 3 0 0 1 2.7 16l-.6-3A5 5 0 0 1 7 7Zm0 3v1.5H5.5v1H7V14h1v-1.5h1.5v-1H8V10H7Zm9 .5a1 1 0 1 0 0 2 1 1 0 0 0 0-2Zm2 2a1 1 0 1 0 0 2 1 1 0 0 0 0-2Z",
        RgbKind.Motherboard =>
            "M4 3h16a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Zm1 2v14h14V5H5Zm2 2h5v5H7V7Zm7 0h3v2h-3V7Zm0 4h3v2h-3v-2Zm-7 3h10v3H7v-3Z",
        RgbKind.Memory =>
            "M3 7h18v9h-2v2h-2v-2h-2v2h-2v-2H9v2H7v-2H5v2H3V7Zm2 2v5h14V9H5Zm1 1h2v3H6v-3Zm4 0h2v3h-2v-3Zm4 0h2v3h-2v-3Z",
        RgbKind.Graphics =>
            "M2 6h18a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6v2H4V8H2V6Zm4 2v8h14V8H6Zm6 1a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
        RgbKind.Cooler =>
            "M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20Zm0 2a8 8 0 1 0 0 16 8 8 0 0 0 0-16Zm0 6.5a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3ZM11.5 6c2 0 3 1.6 2.3 3.6l-.6 1.4h-1.6L11 7.5V6Zm5.4 5.6c1 1.7.1 3.4-2 3.6l-1.5.2-.8-1.4 1.1-2.4h3.2Zm-9.5 3.1c-1.1-1.7-.2-3.4 1.9-3.6l1.5-.2.8 1.4-1.1 2.4H7.4Z",
        RgbKind.Strip =>
            "M2 10h20v4H2v-4Zm3 1v2h2v-2H5Zm4 0v2h2v-2H9Zm4 0v2h2v-2h-2Zm4 0v2h2v-2h-2ZM5.5 7.5 6 5l.5 2.5h-1Zm6 0L12 5l.5 2.5h-1Zm6 0L18 5l.5 2.5h-1Z",
        RgbKind.Case =>
            "M7 2h10a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Zm0 2v16h10V4H7Zm2 2h6v2H9V6Zm3 7a2 2 0 1 1 0 4 2 2 0 0 1 0-4Z",
        RgbKind.Speaker =>
            "M7 2h10a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Zm5 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6Zm0-6a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Z",
        RgbKind.Microphone =>
            "M12 2a3 3 0 0 1 3 3v6a3 3 0 0 1-6 0V5a3 3 0 0 1 3-3Zm-7 9h2a5 5 0 0 0 10 0h2a7 7 0 0 1-6 6.9V21h-2v-3.1A7 7 0 0 1 5 11Z",
        _ =>
            "M12 2a7 7 0 0 1 4 12.7V17H8v-2.3A7 7 0 0 1 12 2Zm-3 17h6v2H9v-2Z",
    };
}
