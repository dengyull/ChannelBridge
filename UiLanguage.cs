using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ChannelBridge;
public static class UiLanguage
{
    public static bool English { get; private set; }
    public static event Action? Changed;
    static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelBridge", "language.txt");
    sealed class Original { public string Text = ""; public bool Updating; }
    static readonly ConditionalWeakTable<Control, Original> originals = new();
    static readonly Dictionary<string, string> words = LoadWords();
    static readonly KeyValuePair<string,string>[] ordered = words.OrderByDescending(p => p.Key.Length).ToArray();
    static Dictionary<string,string> LoadWords()
    {
        using var stream = typeof(UiLanguage).Assembly.GetManifestResourceStream("ChannelBridge.English.tsv")!;
        using var reader = new StreamReader(stream);
        var result = new Dictionary<string,string>();
        while (reader.ReadLine() is string line) { int split = line.IndexOf('\t'); if (split > 0) result[line[..split].Replace("\\n", "\n").Replace("\\r", "\r")] = line[(split+1)..].Replace("\\n", "\n").Replace("\\r", "\r"); }
        return result;
    }
    public static void Load() { try { English = File.Exists(PathName) && File.ReadAllText(PathName).Trim() == "en"; } catch { } }
    public static void Set(bool english, bool persist = true)
    {
        English = english;
        if (persist) try { Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); File.WriteAllText(PathName, english ? "en" : "zh"); } catch { }
        Changed?.Invoke();
    }
    public static string T(string? text)
    {
        if (text == null) return "";
        if (!English) return text;
        if (words.TryGetValue(text, out string? whole)) return whole;
        foreach (var pair in ordered) text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return text;
    }
    public static void Bind(Control control)
    {
        if (!originals.TryGetValue(control, out _))
        {
            var state = new Original { Text = control.Text }; originals.Add(control, state);
            control.TextChanged += (_, _) => { if (state.Updating) return; state.Text = control.Text; ApplyOne(control, state); };
        }
        foreach (Control child in control.Controls) Bind(child);
        Apply(control);
    }
    static void ApplyOne(Control control, Original state)
    {
        if (control is ComboBox || control is NumericUpDown) return;
        state.Updating = true; try { control.Text = T(state.Text); } finally { state.Updating = false; }
    }
    public static void Apply(Control control)
    {
        if (originals.TryGetValue(control, out var state)) ApplyOne(control, state);
        foreach (Control child in control.Controls) Apply(child);
        control.Invalidate();
    }
}
public static class UiMessage
{
    public static DialogResult Show(string text) => MessageBox.Show(UiLanguage.T(text));
    public static DialogResult Show(IWin32Window owner, string text) => MessageBox.Show(owner, UiLanguage.T(text));
    public static DialogResult Show(string text,string caption,MessageBoxButtons buttons = MessageBoxButtons.OK,MessageBoxIcon icon = MessageBoxIcon.None) => MessageBox.Show(UiLanguage.T(text),UiLanguage.T(caption),buttons,icon);
    public static DialogResult Show(IWin32Window owner,string text,string caption,MessageBoxButtons buttons = MessageBoxButtons.OK,MessageBoxIcon icon = MessageBoxIcon.None) => MessageBox.Show(owner,UiLanguage.T(text),UiLanguage.T(caption),buttons,icon);
}
