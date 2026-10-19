using System;

namespace SpotiBee
{
    /// <summary>
    /// A message that stays on the panel until it's clicked or hidden, such as "a new version is
    /// out". Unlike status messages, it doesn't fade after a few seconds.
    /// </summary>
    public sealed class PanelNotice
    {
        /// <summary>Identifies the notice; showing another with the same key replaces it.</summary>
        public string Key { get; set; }
        public string Text { get; set; }
        public string ToolTip { get; set; }

        /// <summary>Runs when the notice is clicked. The notice is cleared first.</summary>
        public Action Clicked { get; set; }

        /// <summary>Runs when the user hides the notice with its ✕.</summary>
        public Action Dismissed { get; set; }
    }
}
