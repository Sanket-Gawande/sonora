using System;
using System.Drawing;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace Sonora {
 // Notification-area icon with a dark menu that mirrors the notch's state.
 sealed class Tray : IDisposable {
  static readonly Color Back = Color.FromArgb(32, 32, 36), Hover = Color.FromArgb(52, 52, 58), Fore = Color.FromArgb(245, 245, 247), Dim = Color.FromArgb(150, 150, 156);
  readonly Forms.NotifyIcon icon;
  readonly Forms.ContextMenuStrip menu;
  readonly NotchWindow notch;
  readonly Session session;
  readonly MediaSession media;
  readonly Action quit;

  public Tray(NotchWindow notch, Session session, MediaSession media, Action quit) {
   this.notch = notch; this.session = session; this.media = media; this.quit = quit;
   menu = new Forms.ContextMenuStrip { ShowImageMargin = false, BackColor = Back, ForeColor = Fore, Font = new Font("Segoe UI", 9f), Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false } };
   menu.Opening += delegate { Rebuild(); };
   icon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true, ContextMenuStrip = menu };
   icon.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) notch.ToggleFromTray(); };
   session.Changed += delegate { UpdateText(); };
   UpdateText();
  }

  static Icon LoadIcon() {
   using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sonora.Sonora.ico"))
    return new Icon(stream, Forms.SystemInformation.SmallIconSize);
  }

  string Status() {
   switch (session.State) {
    case LinkState.Pairing: return "Pairing over USB · " + session.PairingCode;
    case LinkState.Streaming: return "Streaming to " + session.DeviceName + " · USB";
    case LinkState.Reconnecting: return "Waiting for " + session.DeviceName;
    default: return "Not connected";
   }
  }

  void UpdateText() {
   string text = "Sonora · " + Status();
   icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
  }

  void Rebuild() {
   menu.Items.Clear();
   menu.Items.Add(new Forms.ToolStripLabel("Sonora") { Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = Fore, Margin = new Forms.Padding(4, 6, 0, 0) });
   menu.Items.Add(new Forms.ToolStripLabel(Status()) { ForeColor = Dim, Margin = new Forms.Padding(4, 0, 0, 6) });
   menu.Items.Add(new Forms.ToolStripSeparator());
   Add(menu.Items, notch.IsVisible ? "Open Sonora" : "Show Sonora", "Ctrl+Alt+S", true, delegate { notch.ShowNotch(); notch.Expand(false); });
   var now = media.Current;
   if (now != null) {
    string title = now.Title.Length > 32 ? now.Title.Substring(0, 31) + "…" : now.Title;
    Add(menu.Items, (now.Playing ? "Pause " : "Play ") + title, null, now.CanToggle, media.TogglePlayPause);
    Add(menu.Items, "Next track", null, now.CanNext, media.Next);
    menu.Items.Add(new Forms.ToolStripSeparator());
   }
   if (session.State != LinkState.Offline)
    Add(menu.Items, "Disconnect " + session.DeviceName, null, true, delegate { session.Disconnect(); });
   Add(menu.Items, "Connect over USB", null, session.State == LinkState.Offline && !session.Busy, delegate { session.ConnectUsb(); notch.ShowNotch(); notch.Expand(false); });
   Add(menu.Items, "Settings", null, true, delegate { notch.ShowNotch(); notch.OpenSettings(false); });
   if (notch.IsVisible) Add(menu.Items, "Hide Sonora", null, true, delegate { notch.HideNotch(); });

   menu.Items.Add(new Forms.ToolStripSeparator());
   Add(menu.Items, "Quit Sonora", null, true, quit);
  }

  static void Add(Forms.ToolStripItemCollection items, string text, string keys, bool enabled, Action action) {
   var item = new Forms.ToolStripMenuItem(text) { Enabled = enabled, ForeColor = enabled ? Fore : Dim, Padding = new Forms.Padding(4, 3, 4, 3) };
   if (keys != null) item.ShortcutKeyDisplayString = keys;
   item.Click += delegate { action(); };
   items.Add(item);
  }

  public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); }

  sealed class DarkColors : Forms.ProfessionalColorTable {
   public override Color ToolStripDropDownBackground { get { return Back; } }
   public override Color MenuBorder { get { return Color.FromArgb(60, 60, 66); } }
   public override Color MenuItemBorder { get { return Hover; } }
   public override Color MenuItemSelected { get { return Hover; } }
   public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
   public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
   public override Color MenuItemPressedGradientBegin { get { return Hover; } }
   public override Color MenuItemPressedGradientEnd { get { return Hover; } }
   public override Color ImageMarginGradientBegin { get { return Back; } }
   public override Color ImageMarginGradientMiddle { get { return Back; } }
   public override Color ImageMarginGradientEnd { get { return Back; } }
   public override Color SeparatorDark { get { return Color.FromArgb(60, 60, 66); } }
   public override Color SeparatorLight { get { return Back; } }
  }
 }
}
