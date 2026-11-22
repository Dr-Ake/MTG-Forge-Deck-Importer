using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ForgeDeckImporter
{
    internal sealed class CardInfo
    {
        public string Name;
        public bool Basic;
        public bool Land;
        public bool CommanderCandidate;
    }

    internal sealed class CardEntry
    {
        public int Quantity;
        public string Name;
    }

    internal sealed class Catalog
    {
        public readonly Dictionary<string, CardInfo> Cards = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CardInfo> aliases = new Dictionary<string, CardInfo>(StringComparer.OrdinalIgnoreCase);

        public static string Normalize(string text)
        {
            return Regex.Replace(text.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201c', '"').Replace('\u201d', '"'), @"\s*//\s*", " // ");
        }

        public bool TryResolve(string name, out CardInfo card)
        {
            string key = Normalize(name);
            return Cards.TryGetValue(key, out card) || aliases.TryGetValue(key, out card);
        }

        public CardInfo AddFaces(List<string> faces, string types, string alternateMode, string frontRules = "")
        {
            if (faces.Count == 0) return null;
            string combined = faces.Count == 2 ? String.Join(" // ", faces) : null;
            string canonical = alternateMode == "Split" && combined != null ? combined : faces[0];
            bool legendary = Regex.IsMatch(types, @"\bLegendary\b");
            bool creature = Regex.IsMatch(types, @"\bCreature\b") || frontRules.Split('\n').Any(line => line.StartsWith("S:", StringComparison.Ordinal) && line.Contains("CharacteristicDefining$ True") && line.Contains("AddType$ Creature"));
            bool vehicle = Regex.IsMatch(types, @"\bVehicle\b");
            bool spacecraft = Regex.IsMatch(types, @"\bSpacecraft\b") && Regex.IsMatch(frontRules, @"(?m)^PT:[^/\r\n]+/");
            bool explicitCommander = frontRules.Split('\n').Any(line => line.StartsWith("Oracle:", StringComparison.Ordinal) && line.IndexOf("can be your commander", StringComparison.OrdinalIgnoreCase) >= 0);
            CardInfo card = new CardInfo { Name = canonical, Basic = Regex.IsMatch(types, @"\bBasic\b"), Land = Regex.IsMatch(types, @"\bLand\b"), CommanderCandidate = !Regex.IsMatch(types, @"\bBackground\b") && (explicitCommander || (legendary && (creature || vehicle || spacecraft))) };
            Cards[Normalize(canonical)] = card;
            if (combined != null && !String.Equals(combined, canonical, StringComparison.OrdinalIgnoreCase)) aliases[Normalize(combined)] = card;
            return card;
        }

        public static Catalog Load(string forgeFolder)
        {
            string zipPath = Path.Combine(forgeFolder, "res", "cardsfolder", "cardsfolder.zip");
            if (!File.Exists(zipPath)) throw new InvalidOperationException("That folder does not contain Forge's card database. Choose the folder containing forge.exe and res.");
            Catalog catalog = new Catalog();
            Dictionary<string, string> faceTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<CardInfo, string>> copiedTypes = new List<KeyValuePair<CardInfo, string>>();
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                    List<string> faces = new List<string>();
                    string types = "";
                    string alternateMode = "";
                    string currentFace = null;
                    StringBuilder frontRules = new StringBuilder();
                    bool inFront = true;
                    bool nextFace = true;
                    using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Trim() == "ALTERNATE") { nextFace = true; inFront = false; }
                            else if (nextFace && (line.StartsWith("Name:", StringComparison.Ordinal) || line.StartsWith("CopyFaceFrom:", StringComparison.Ordinal))) { currentFace = line.Substring(line.IndexOf(':') + 1).Trim(); faces.Add(currentFace); nextFace = false; }
                            else if (currentFace != null && line.StartsWith("Types:", StringComparison.Ordinal)) { string faceType = line.Substring(6).Trim(); faceTypes[Normalize(currentFace)] = faceType; if (faces.Count == 1 && types.Length == 0) types = faceType; }
                            else if (line.StartsWith("AlternateMode:", StringComparison.Ordinal)) alternateMode = line.Substring(14).Trim();
                            if (inFront) frontRules.AppendLine(line);
                        }
                    }
                    CardInfo added = catalog.AddFaces(faces, types, alternateMode, frontRules.ToString());
                    if (added != null && types.Length == 0) copiedTypes.Add(new KeyValuePair<CardInfo, string>(added, faces[0]));
                }
            }
            foreach (KeyValuePair<CardInfo, string> copied in copiedTypes)
            {
                string inherited;
                if (faceTypes.TryGetValue(Normalize(copied.Value), out inherited)) { copied.Key.Basic = Regex.IsMatch(inherited, @"\bBasic\b"); copied.Key.Land = Regex.IsMatch(inherited, @"\bLand\b"); }
            }
            if (catalog.Cards.Count < 100) throw new InvalidOperationException("Forge's card database could not be read correctly. Choose a complete Forge installation.");
            return catalog;
        }
    }

    internal static class DeckParser
    {
        private static readonly Regex CardLine = new Regex(@"^(\d+)\s*[xX]?\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex(@"^(commander|main|mainboard|deck|creatures?|artifacts?|enchantments?|instants?|sorceries|planeswalkers?|lands?|sideboard)(\s*\(\d+\))?\s*:?$", RegexOptions.IgnoreCase);

        public static List<CardEntry> Parse(string text)
        {
            List<CardEntry> entries = new List<CardEntry>();
            Dictionary<string, CardEntry> byName = new Dictionary<string, CardEntry>(StringComparer.OrdinalIgnoreCase);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim().TrimEnd('\\').Trim();
                if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal) || Heading.IsMatch(line)) continue;
                Match match = CardLine.Match(line);
                int quantity;
                if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out quantity) || quantity < 1 || quantity > 1000)
                    throw new InvalidOperationException("Line " + (index + 1) + " needs a positive quantity and card name, for example: 1 Sol Ring.");
                string name = Catalog.Normalize(match.Groups[2].Value);
                if (name.Length == 0 || name.Length > 250 || name.IndexOf('|') >= 0 || name.Any(Char.IsControl))
                    throw new InvalidOperationException("Line " + (index + 1) + " contains an invalid card name. Paste card names without set codes or collector numbers.");
                CardEntry existing;
                if (byName.TryGetValue(name, out existing)) existing.Quantity = checked(existing.Quantity + quantity);
                else
                {
                    CardEntry item = new CardEntry { Quantity = quantity, Name = name };
                    byName.Add(name, item);
                    entries.Add(item);
                }
            }
            if (entries.Count == 0) throw new InvalidOperationException("Paste your deck list first, including the commander.");
            return entries;
        }

        public static List<CardEntry> Validate(string text, string commander, Catalog catalog)
        {
            List<CardEntry> entries = Parse(text);
            int total = entries.Sum(e => e.Quantity);
            if (total != 100) throw new InvalidOperationException("This list has " + total + " cards. A Commander deck needs 100 cards including its commander.");
            List<string> missing = entries.Where(e => { CardInfo card; return !catalog.TryResolve(e.Name, out card); }).Select(e => e.Name).ToList();
            if (missing.Count != 0) throw new InvalidOperationException("These cards are not in this Forge installation:\r\n\r\n" + String.Join("\r\n", missing.Take(20)) + (missing.Count > 20 ? "\r\n…and " + (missing.Count - 20) + " more." : "") + "\r\n\r\nCheck the spelling or update Forge before importing. No cards have been removed.");
            entries = ResolveKnown(entries, catalog);
            CardInfo commanderCard;
            string selectedName = catalog.TryResolve(commander, out commanderCard) ? commanderCard.Name : Catalog.Normalize(commander);
            CardEntry selected = entries.FirstOrDefault(e => String.Equals(e.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            if (selected == null || selected.Quantity != 1) throw new InvalidOperationException("Select a commander that appears exactly once in the pasted list.");
            return entries;
        }

        public static List<CardEntry> ResolveKnown(List<CardEntry> entries, Catalog catalog)
        {
            List<CardEntry> result = new List<CardEntry>();
            Dictionary<string, CardEntry> byName = new Dictionary<string, CardEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (CardEntry entry in entries)
            {
                CardInfo card;
                string name = catalog.TryResolve(entry.Name, out card) ? card.Name : entry.Name;
                CardEntry existing;
                if (byName.TryGetValue(name, out existing)) existing.Quantity = checked(existing.Quantity + entry.Quantity);
                else { CardEntry item = new CardEntry { Name = name, Quantity = entry.Quantity }; result.Add(item); byName.Add(name, item); }
            }
            return result;
        }

        public static List<string> ValidateAlternates(List<CardEntry> entries, string commander, IEnumerable<string> choices, Catalog catalog)
        {
            List<string> result = new List<string>();
            if (choices == null) return result;
            CardInfo defaultCard;
            string defaultName = catalog.TryResolve(commander, out defaultCard) ? defaultCard.Name : commander;
            foreach (string choice in choices)
            {
                CardInfo card;
                if (!catalog.TryResolve(choice, out card) || !card.CommanderCandidate) throw new InvalidOperationException("Choose alternate leaders from the commander choices shown below your deck list.");
                if (String.Equals(card.Name, defaultName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The default commander is already selected. Choose other cards as alternates.");
                CardEntry entry = entries.FirstOrDefault(e => String.Equals(e.Name, card.Name, StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Quantity != 1) throw new InvalidOperationException("Each alternate commander must appear exactly once in the pasted list: " + card.Name);
                if (card.Name.IndexOf(';') >= 0 || card.Name.Any(Char.IsControl)) throw new InvalidOperationException("Forge cannot save this alternate commander name: " + card.Name);
                if (!result.Contains(card.Name, StringComparer.OrdinalIgnoreCase)) result.Add(card.Name);
            }
            return result;
        }
    }

    internal static class ForgePaths
    {
        public static string Discover()
        {
            foreach (string desktop in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                if (!Directory.Exists(desktop)) continue;
                foreach (string shortcut in Directory.GetFiles(desktop, "*Forge*.lnk"))
                {
                    object shell = null;
                    object link = null;
                    try
                    {
                        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                        if (shellType == null) continue;
                        shell = Activator.CreateInstance(shellType);
                        link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcut });
                        string target = Convert.ToString(link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null));
                        string folder = Path.GetDirectoryName(target);
                        if (IsForgeFolder(folder)) return Path.GetFullPath(folder);
                    }
                    catch { }
                    finally
                    {
                        if (link != null && System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                        if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                    }
                }
            }
            if (IsForgeFolder(@"D:\Forge")) return @"D:\Forge";
            if (IsForgeFolder(AppDomain.CurrentDomain.BaseDirectory)) return AppDomain.CurrentDomain.BaseDirectory;
            return "";
        }

        private static bool IsForgeFolder(string folder)
        {
            return !String.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "res", "cardsfolder", "cardsfolder.zip"));
        }

        private static string ReadProperty(string forgeFolder, string key)
        {
            string profile = Path.Combine(forgeFolder, "forge.profile.properties");
            if (!File.Exists(profile)) return "";
            foreach (string raw in File.ReadAllLines(profile))
            {
                string line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("!", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                if (equals >= 0 && String.Equals(line.Substring(0, equals).Trim(), key, StringComparison.Ordinal))
                    return line.Substring(equals + 1).Trim().Replace("\\:", ":").Replace("\\\\", "\\");
            }
            return "";
        }

        private static string Resolve(string value, string forgeFolder)
        {
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(forgeFolder, value));
        }

        public static string CommanderFolder(string forgeFolder)
        {
            string user = ReadProperty(forgeFolder, "userDir");
            string userFolder = String.IsNullOrWhiteSpace(user) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge") : Resolve(user, forgeFolder);
            string decks = ReadProperty(forgeFolder, "decksDir");
            string decksFolder = String.IsNullOrWhiteSpace(decks) ? Path.Combine(userFolder, "decks") : Resolve(decks, forgeFolder);
            return Path.GetFullPath(Path.Combine(decksFolder, "commander"));
        }
    }

    internal static class DeckWriter
    {
        public static string SafeFileName(string deckName)
        {
            string clean = new string(deckName.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !Char.IsControl(c)).ToArray()).Trim().TrimEnd('.', ' ');
            clean = Regex.Replace(clean, @"\s+", " ");
            if (String.IsNullOrWhiteSpace(clean)) throw new InvalidOperationException("Enter a deck name containing letters or numbers.");
            if (clean.Length > 100) clean = clean.Substring(0, 100).TrimEnd('.', ' ');
            if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase)) clean = "Deck " + clean;
            return clean + ".dck";
        }

        public static string FindTarget(string commanderFolder, string deckName)
        {
            string normalizedName = deckName.Trim();
            if (normalizedName.Any(Char.IsControl) || normalizedName.Length == 0 || normalizedName.Length > 150) throw new InvalidOperationException("Use a deck name between 1 and 150 characters without line breaks.");
            List<string> matches = new List<string>();
            if (Directory.Exists(commanderFolder))
            {
                foreach (string path in Directory.GetFiles(commanderFolder, "*.dck"))
                {
                    foreach (string line in File.ReadLines(path).Take(30))
                    {
                        if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && String.Equals(line.Substring(5).Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)) { matches.Add(path); break; }
                    }
                }
            }
            if (matches.Count > 1) throw new InvalidOperationException("Several saved decks have that name. Enter a different deck name to keep them separate.");
            string target = matches.Count == 1 ? matches[0] : Path.Combine(commanderFolder, SafeFileName(normalizedName));
            string fullFolder = Path.GetFullPath(commanderFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            target = Path.GetFullPath(target);
            if (!target.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetDirectoryName(target), fullFolder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The deck name does not produce a safe save location.");
            return target;
        }

        public static string Format(string deckName, string commander, List<CardEntry> entries)
        {
            return Format(deckName, commander, entries, null);
        }

        public static string Format(string deckName, string commander, List<CardEntry> entries, IEnumerable<string> alternateCommanders)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("[metadata]").AppendLine("Name=" + deckName.Trim());
            if (alternateCommanders != null && alternateCommanders.Any()) text.AppendLine("AltCommanders=" + String.Join(";", alternateCommanders));
            text.AppendLine("[Commander]").AppendLine("1 " + commander).AppendLine("[Main]");
            foreach (CardEntry entry in entries)
            {
                if (!String.Equals(entry.Name, commander, StringComparison.OrdinalIgnoreCase)) text.AppendLine(entry.Quantity.ToString(CultureInfo.InvariantCulture) + " " + entry.Name);
            }
            text.AppendLine("[Sideboard]");
            return text.ToString();
        }

        public static string Save(string folder, string target, string content, bool allowReplace)
        {
            Directory.CreateDirectory(folder);
            bool existed = File.Exists(target);
            if (existed && !allowReplace) throw new InvalidOperationException("A deck with this name already exists. Choose a different name or allow replacement.");
            string temp = Path.Combine(folder, ".deck-import-" + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = null;
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (existed)
                {
                    string backupFolder = Path.Combine(Directory.GetParent(folder).FullName, "_deck-importer-backups");
                    Directory.CreateDirectory(backupFolder);
                    backup = Path.Combine(backupFolder, Path.GetFileName(target) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bak");
                    File.Copy(target, backup, false);
                    File.Replace(temp, target, null);
                }
                else File.Move(temp, target);
                return backup;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class ImporterForm : Form
    {
        private readonly TextBox deckText = new TextBox();
        private readonly TextBox deckName = new TextBox();
        private readonly ComboBox commander = new ComboBox();
        private readonly CheckedListBox alternateCommanders = new CheckedListBox();
        private readonly Label folderLabel = new Label();
        private readonly Label summary = new Label();
        private readonly Label catalogLabel = new Label();
        private readonly Button import = new Button();
        private readonly Button browse = new Button();
        private readonly Button openFolder = new Button();
        private readonly Timer parseTimer = new Timer();
        private Catalog catalog;
        private string forgeFolder;
        private string commanderFolder;
        private string lastAutomaticName = "";
        private List<CardEntry> previewEntries = new List<CardEntry>();
        private bool updatingPreview;

        public ImporterForm()
        {
            Text = "Forge Deck Importer";
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(typeof(ImporterForm).Assembly.Location);
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(860, 860);
            MinimumSize = new Size(700, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(246, 248, 251);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 15 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            Controls.Add(layout);
            AddWide(layout, new Label { Text = "Paste a deck. Add it to Forge.", Font = new Font("Segoe UI Semibold", 21F), AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0);
            AddWide(layout, new Label { Text = "Commander decks · 100 cards including the commander", AutoSize = true, ForeColor = Color.FromArgb(85, 96, 111), Margin = new Padding(0, 0, 0, 18) }, 1);
            folderLabel.AutoEllipsis = true; folderLabel.Dock = DockStyle.Fill; folderLabel.Margin = new Padding(0, 8, 10, 0);
            layout.Controls.Add(folderLabel, 0, 2);
            browse.Text = "Choose Forge…"; browse.Dock = DockStyle.Fill; browse.AutoSize = true; browse.Click += ChooseForge; layout.Controls.Add(browse, 1, 2);
            catalogLabel.AutoSize = true; catalogLabel.ForeColor = Color.FromArgb(85, 96, 111); catalogLabel.Margin = new Padding(0, 5, 0, 14); AddWide(layout, catalogLabel, 3);
            deckText.Multiline = true; deckText.AcceptsReturn = true; deckText.AcceptsTab = false; deckText.ScrollBars = ScrollBars.Vertical; deckText.WordWrap = false; deckText.Font = new Font("Consolas", 10F); deckText.Dock = DockStyle.Fill; deckText.Margin = new Padding(0, 0, 0, 8);
            AddWide(layout, deckText, 4); layout.RowStyles.Add(new RowStyle()); layout.RowStyles.Add(new RowStyle()); layout.RowStyles.Add(new RowStyle()); layout.RowStyles.Add(new RowStyle()); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            summary.AutoSize = true; summary.ForeColor = Color.FromArgb(85, 96, 111); summary.Margin = new Padding(0, 0, 0, 13); AddWide(layout, summary, 5);
            AddWide(layout, new Label { Text = "Commander (choose yours if the list is alphabetized)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 6);
            commander.DropDownStyle = ComboBoxStyle.DropDownList; commander.Dock = DockStyle.Fill; commander.Margin = new Padding(0, 0, 0, 12); AddWide(layout, commander, 7);
            AddWide(layout, new Label { Text = "Alternate commander choices (optional)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 8);
            alternateCommanders.CheckOnClick = true; alternateCommanders.Dock = DockStyle.Fill; alternateCommanders.Height = 88; alternateCommanders.IntegralHeight = false; alternateCommanders.Margin = new Padding(0, 0, 0, 6); alternateCommanders.Enabled = false; AddWide(layout, alternateCommanders, 9);
            AddWide(layout, new Label { Text = "Tick other leaders in the list. Forge offers legal choices with its Commander button before a game.", AutoSize = true, ForeColor = Color.FromArgb(85, 96, 111), Margin = new Padding(0, 0, 0, 12) }, 10);
            AddWide(layout, new Label { Text = "Deck name", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 11);
            deckName.Dock = DockStyle.Fill; deckName.MaxLength = 150; deckName.Margin = new Padding(0, 0, 0, 16); AddWide(layout, deckName, 12);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            import.Text = "Add to Forge"; import.AutoSize = true; import.Padding = new Padding(20, 8, 20, 8); import.BackColor = Color.FromArgb(27, 104, 170); import.ForeColor = Color.White; import.FlatStyle = FlatStyle.Flat; import.FlatAppearance.BorderSize = 0; import.Enabled = false; import.Click += ImportDeck; buttons.Controls.Add(import);
            openFolder.Text = "Open deck folder"; openFolder.AutoSize = true; openFolder.Padding = new Padding(12, 8, 12, 8); openFolder.Margin = new Padding(12, 0, 0, 0); openFolder.Enabled = false; openFolder.Click += delegate { try { Directory.CreateDirectory(commanderFolder); System.Diagnostics.Process.Start("explorer.exe", "\"" + commanderFolder + "\""); } catch (Exception error) { ShowError(error.Message); } }; buttons.Controls.Add(openFolder); AddWide(layout, buttons, 13);
            AddWide(layout, new Label { Text = "Saves on this PC only. No account, downloads, or internet connection needed.", AutoSize = true, ForeColor = Color.FromArgb(85, 96, 111), Margin = new Padding(0, 15, 0, 0) }, 14);
            for (int row = layout.RowStyles.Count; row < 15; row++) layout.RowStyles.Add(new RowStyle(row == 9 ? SizeType.Absolute : SizeType.AutoSize, row == 9 ? 88 : 0));
            parseTimer.Interval = 350; parseTimer.Tick += delegate { parseTimer.Stop(); UpdatePreview(); };
            deckText.TextChanged += delegate { parseTimer.Stop(); parseTimer.Start(); };
            commander.SelectedIndexChanged += delegate { if (!updatingPreview) { UpdateAutomaticName(); UpdateAlternateChoices(); } };
            Shown += delegate { LoadForge(ForgePaths.Discover()); };
            UpdatePreview();
        }

        private static void AddWide(TableLayoutPanel layout, Control control, int row) { layout.Controls.Add(control, 0, row); layout.SetColumnSpan(control, 2); }
        private void ShowError(string message) { MessageBox.Show(this, message, "Could not add deck", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        private void UpdateAutomaticName()
        {
            string selected = Convert.ToString(commander.SelectedItem);
            if (deckName.Text.Length == 0 || deckName.Text == lastAutomaticName) { deckName.Text = selected; lastAutomaticName = selected; }
        }

        private void UpdateAlternateChoices()
        {
            HashSet<string> checkedNames = new HashSet<string>(alternateCommanders.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            string selected = Convert.ToString(commander.SelectedItem);
            alternateCommanders.BeginUpdate();
            try
            {
                alternateCommanders.Items.Clear();
                if (catalog == null) return;
                foreach (CardEntry entry in previewEntries.Where(e => e.Quantity == 1 && !String.Equals(e.Name, selected, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    CardInfo card;
                    if (catalog.TryResolve(entry.Name, out card) && card.CommanderCandidate) alternateCommanders.Items.Add(card.Name, checkedNames.Contains(card.Name));
                }
            }
            finally { alternateCommanders.EndUpdate(); }
        }

        private void ChooseForge(object sender, EventArgs args)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Choose the Forge installation folder (the folder containing forge.exe and res).", SelectedPath = forgeFolder ?? "", ShowNewFolderButton = false })
            { if (dialog.ShowDialog(this) == DialogResult.OK) LoadForge(dialog.SelectedPath); }
        }

        private void LoadForge(string folder)
        {
            catalog = null; import.Enabled = false; openFolder.Enabled = false; alternateCommanders.Enabled = false;
            if (String.IsNullOrEmpty(folder)) { folderLabel.Text = "Choose your Forge installation to begin."; catalogLabel.Text = "Forge was not found automatically."; return; }
            forgeFolder = folder;
            try { commanderFolder = ForgePaths.CommanderFolder(folder); }
            catch (Exception error) { catalogLabel.Text = error.Message; return; }
            folderLabel.Text = "Forge: " + folder;
            catalogLabel.Text = "Loading Forge's card list…";
            browse.Enabled = false;
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs args) { args.Result = Catalog.Load(folder); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args)
            {
                browse.Enabled = true;
                if (args.Error != null) { catalogLabel.Text = args.Error.Message; return; }
                catalog = (Catalog)args.Result;
                catalogLabel.Text = catalog.Cards.Count.ToString("N0") + " supported card names · Saves to Commander decks";
                import.Enabled = true; openFolder.Enabled = true; alternateCommanders.Enabled = true; UpdatePreview();
            };
            worker.RunWorkerAsync();
        }

        private void UpdatePreview()
        {
            if (String.IsNullOrWhiteSpace(deckText.Text)) { summary.Text = "Paste quantities and names, for example: 1 Sol Ring"; previewEntries.Clear(); commander.Items.Clear(); alternateCommanders.Items.Clear(); return; }
            try
            {
                List<CardEntry> entries = DeckParser.Parse(deckText.Text);
                if (catalog != null) entries = DeckParser.ResolveKnown(entries, catalog);
                string previous = Convert.ToString(commander.SelectedItem);
                CardInfo previousCard;
                if (catalog != null && catalog.TryResolve(previous, out previousCard)) previous = previousCard.Name;
                previewEntries = entries;
                updatingPreview = true; commander.BeginUpdate();
                try
                {
                    commander.Items.Clear();
                    foreach (CardEntry entry in entries) commander.Items.Add(entry.Name);
                    int match = entries.FindIndex(e => String.Equals(e.Name, previous, StringComparison.OrdinalIgnoreCase));
                    commander.SelectedIndex = match >= 0 ? match : 0;
                }
                finally { commander.EndUpdate(); updatingPreview = false; }
                UpdateAutomaticName(); UpdateAlternateChoices();
                int total = entries.Sum(e => e.Quantity);
                int unknown = catalog == null ? 0 : entries.Count(e => !catalog.Cards.ContainsKey(e.Name));
                int lands = catalog == null ? 0 : entries.Where(e => catalog.Cards.ContainsKey(e.Name) && catalog.Cards[e.Name].Land).Sum(e => e.Quantity);
                summary.Text = total + " / 100 cards" + (catalog == null ? "" : " · " + lands + " lands · " + (unknown == 0 ? "All card names supported" : unknown + " unknown card names"));
                summary.ForeColor = total == 100 && unknown == 0 ? Color.FromArgb(31, 118, 73) : Color.FromArgb(156, 80, 12);
            }
            catch (Exception error) { summary.Text = error.Message; summary.ForeColor = Color.FromArgb(156, 80, 12); }
        }

        private void ImportDeck(object sender, EventArgs args)
        {
            if (catalog == null) return;
            parseTimer.Stop(); UpdatePreview();
            try
            {
                string selected = Catalog.Normalize(Convert.ToString(commander.SelectedItem));
                List<CardEntry> entries = DeckParser.Validate(deckText.Text, selected, catalog);
                CardInfo selectedCard;
                if (!catalog.TryResolve(selected, out selectedCard)) throw new InvalidOperationException("Choose your commander from the pasted list.");
                selected = selectedCard.Name;
                List<string> alternateChoices = DeckParser.ValidateAlternates(entries, selected, alternateCommanders.CheckedItems.Cast<string>(), catalog);
                string title = deckName.Text.Trim();
                string target = DeckWriter.FindTarget(commanderFolder, title);
                List<string> duplicates = entries.Where(e => e.Quantity > 1 && !catalog.Cards[e.Name].Basic).Select(e => e.Quantity + " " + e.Name).ToList();
                if (duplicates.Count > 0 && MessageBox.Show(this, "This list repeats these nonbasic cards:\r\n\r\n" + String.Join("\r\n", duplicates) + "\r\n\r\nSome cards allow multiple copies. Keep this list exactly as pasted?", "Check repeated cards", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                bool exists = File.Exists(target);
                if (exists && MessageBox.Show(this, "A saved deck already uses this name.\r\n\r\nReplace it with this list? A backup of the old deck will be kept.\r\n\r\nChoose No to keep it and enter a different deck name.", "Replace existing deck?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string backup = DeckWriter.Save(commanderFolder, target, DeckWriter.Format(title, selected, entries, alternateChoices), exists);
                string choicesNote = alternateChoices.Count == 0 ? "" : "\r\n\r\nAlternate commanders: " + String.Join(", ", alternateChoices) + "\r\nUse Forge's Commander button before a game to pick a legal alternative.";
                MessageBox.Show(this, "Added to Forge:\r\n" + title + "\r\n\r\n100 cards · " + selected + " as default commander" + choicesNote + "\r\n\r\n" + target + "\r\n\r\nIt will appear when Forge next opens. If Forge is open, finish your game and restart it." + (backup == null ? "" : "\r\n\r\nThe previous deck was backed up to:\r\n" + backup), "Deck added", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException) { ShowError("Windows could not write to the Commander folder. Choose a Forge installation in a folder you can write to."); }
            catch (IOException error) { ShowError("The deck could not be saved. Your existing deck has been kept.\r\n\r\n" + error.Message); }
            catch (Exception error) { ShowError(error.Message); }
        }
    }

    internal static class SelfTests
    {
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void MustFail(Action action, string description) { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } Assert(failed, description); }

        public static string Run(string directory)
        {
            Directory.CreateDirectory(directory);
            Catalog catalog = new Catalog();
            catalog.Cards.Add("Test Commander", new CardInfo { Name = "Test Commander" });
            catalog.Cards.Add("Mountain", new CardInfo { Name = "Mountain", Basic = true, Land = true });
            catalog.Cards.Add("Mind's Eye", new CardInfo { Name = "Mind's Eye" });
            string pasted = "```text\r\nCOMMANDER (1)\r\n1 Test Commander\\\r\nLANDS (99)\r\n99x Mountain\\\r\n```";
            List<CardEntry> entries = DeckParser.Validate(pasted, "Test Commander", catalog);
            Assert(entries.Sum(e => e.Quantity) == 100, "Trailing backslashes / CRLF / headings / x quantities failed.");
            Assert(DeckParser.Parse("1 Mind\u2019s Eye")[0].Name == "Mind's Eye", "Curly apostrophe normalization failed.");
            Assert(DeckParser.Parse("1 Mountain\n2 Mountain")[0].Quantity == 3, "Repeated-line aggregation failed.");
            MustFail(delegate { DeckParser.Validate("1 Test Commander\n98 Mountain", "Test Commander", catalog); }, "99-card list was accepted.");
            MustFail(delegate { DeckParser.Validate("1 Test Commander\n98 Mountain\n1 Unknown Card", "Test Commander", catalog); }, "Unknown card was accepted.");
            MustFail(delegate { DeckParser.Validate("2 Test Commander\n98 Mountain", "Test Commander", catalog); }, "Duplicate commander was accepted.");
            MustFail(delegate { DeckParser.Parse("0 Mountain"); }, "Zero quantity was accepted.");
            MustFail(delegate { DeckParser.Parse("1 Mountain\naccidental prose"); }, "Invalid line was silently dropped.");
            List<CardEntry> tabbed = DeckParser.Validate("99x\tMountain\\\n1x\tTest Commander\\", "Test Commander", catalog);
            Assert(tabbed.Sum(e => e.Quantity) == 100 && DeckWriter.Format("Tabbed deck", "Test Commander", tabbed).Contains("[Commander]\r\n1 Test Commander\r\n[Main]\r\n99 Mountain"), "Tabbed x quantities or a commander later in the list failed.");
            catalog.AddFaces(new List<string> { "Howlpack Piper", "Wildsong Howler" }, "Creature Human Werewolf", "DoubleFaced");
            catalog.AddFaces(new List<string> { "Fire", "Ice" }, "Instant", "Split");
            catalog.AddFaces(new List<string> { "Bonecrusher Giant", "Stomp" }, "Creature Giant", "Adventure");
            catalog.AddFaces(new List<string> { "Test Modal Front", "Test Modal Back" }, "Land", "Modal");
            List<CardEntry> faces = DeckParser.Validate("1 Test Commander\n1x\tHowlpack Piper//Wildsong Howler\\\n1 Fire // Ice\n1 Bonecrusher Giant // Stomp\n1 Test Modal Front // Test Modal Back\n95 Mountain", "Test Commander", catalog);
            string faceDeck = DeckWriter.Format("Faces", "Test Commander", faces);
            Assert(faceDeck.Contains("1 Howlpack Piper\r\n") && !faceDeck.Contains("Wildsong Howler"), "Double-faced names were not serialized as Forge front names.");
            Assert(faceDeck.Contains("1 Fire // Ice\r\n"), "Split-card names were truncated.");
            Assert(faceDeck.Contains("1 Bonecrusher Giant\r\n") && faceDeck.Contains("1 Test Modal Front\r\n"), "Adventure or modal names were not resolved.");
            MustFail(delegate { DeckParser.Validate("1 Test Commander\n1 Howlpack Piper // Wrong Back\n98 Mountain", "Test Commander", catalog); }, "A mismatched second face was accepted.");
            List<CardEntry> combinedAliases = DeckParser.Validate("1 Test Commander\n1 Howlpack Piper\n1 Howlpack Piper // Wildsong Howler\n97 Mountain", "Test Commander", catalog);
            Assert(combinedAliases.Count(e => e.Name == "Howlpack Piper") == 1 && combinedAliases.Single(e => e.Name == "Howlpack Piper").Quantity == 2, "Aliases for the same physical card were not combined.");
            MustFail(delegate { DeckParser.Validate("1 Howlpack Piper\n1 Howlpack Piper // Wildsong Howler\n98 Mountain", "Howlpack Piper // Wildsong Howler", catalog); }, "Duplicate commander aliases were accepted.");
            catalog.AddFaces(new List<string> { "Specialize Front", "Specialize Back One", "Specialize Back Two" }, "Creature", "Specialize");
            CardInfo unmatched;
            Assert(!catalog.TryResolve("Specialize Front // Specialize Back One", out unmatched), "A two-face alias was invented for a multi-face script.");
            catalog.AddFaces(new List<string> { "Alternate Leader A" }, "Legendary Creature", "");
            catalog.AddFaces(new List<string> { "Alternate Leader B" }, "Legendary Creature", "");
            catalog.AddFaces(new List<string> { "Outside Leader" }, "Legendary Creature", "");
            List<CardEntry> alternateDeck = DeckParser.Validate("1 Test Commander\n1 Alternate Leader A\n1 Alternate Leader B\n97 Mountain", "Test Commander", catalog);
            List<string> alternateNames = DeckParser.ValidateAlternates(alternateDeck, "Test Commander", new[] { "Alternate Leader A", "Alternate Leader B", "alternate leader a" }, catalog);
            string alternateText = DeckWriter.Format("Commander options", "Test Commander", alternateDeck, alternateNames);
            Assert(alternateNames.Count == 2 && alternateText.Contains("AltCommanders=Alternate Leader A;Alternate Leader B\r\n[Commander]\r\n1 Test Commander\r\n[Main]\r\n1 Alternate Leader A\r\n1 Alternate Leader B\r\n97 Mountain"), "Alternate metadata, default Commander slot, or alternate Main cards were incorrect.");
            Assert(alternateDeck.Sum(e => e.Quantity) == 100 && !DeckWriter.Format("Default only", "Test Commander", alternateDeck).Contains("AltCommanders="), "Alternate choices changed card counts or default-only output.");
            MustFail(delegate { DeckParser.ValidateAlternates(alternateDeck, "Test Commander", new[] { "Outside Leader" }, catalog); }, "An alternate not present in the deck was accepted.");
            MustFail(delegate { DeckParser.ValidateAlternates(alternateDeck, "Alternate Leader A", new[] { "Alternate Leader A" }, catalog); }, "The default commander was also accepted as its own alternate.");
            MustFail(delegate { DeckParser.ValidateAlternates(alternateDeck, "Test Commander", new[] { "Mountain" }, catalog); }, "An ineligible alternate was accepted.");
            List<CardEntry> repeatedAlternate = DeckParser.Validate("1 Test Commander\n2 Alternate Leader A\n97 Mountain", "Test Commander", catalog);
            MustFail(delegate { DeckParser.ValidateAlternates(repeatedAlternate, "Test Commander", new[] { "Alternate Leader A" }, catalog); }, "An alternate appearing twice was accepted.");
            Assert(catalog.AddFaces(new List<string> { "Test Vehicle" }, "Legendary Artifact Vehicle", "").CommanderCandidate, "Legendary Vehicle was not offered as a commander choice.");
            Assert(catalog.AddFaces(new List<string> { "Test Spacecraft" }, "Legendary Artifact Spacecraft", "", "PT:6/7").CommanderCandidate, "Powered legendary Spacecraft was not offered as a commander choice.");
            Assert(!catalog.AddFaces(new List<string> { "Test Spacecraft Without Power" }, "Legendary Artifact Spacecraft", "").CommanderCandidate, "Spacecraft without printed power was offered as a commander choice.");
            Assert(catalog.AddFaces(new List<string> { "Test Explicit Commander" }, "Legendary Planeswalker", "", "Oracle:This card can be your commander.").CommanderCandidate, "An explicit commander permission was not recognized.");
            Assert(!catalog.AddFaces(new List<string> { "Test Background" }, "Legendary Enchantment Background", "").CommanderCandidate, "A standalone Background was offered as an alternate leader.");
            string folder = Path.Combine(directory, "decks", "commander");
            Directory.CreateDirectory(folder);
            string target = DeckWriter.FindTarget(folder, "../Test: Commander");
            Assert(Path.GetDirectoryName(target) == Path.GetFullPath(folder), "Unsafe filename escaped Commander folder.");
            string content = DeckWriter.Format("Test Commander", "Test Commander", entries);
            Assert(content.Contains("[Commander]\r\n1 Test Commander\r\n[Main]\r\n99 Mountain"), "Commander / main serialization failed.");
            target = Path.Combine(folder, "existing-underscore-name.dck");
            DeckWriter.Save(folder, target, content, false);
            Assert(DeckWriter.FindTarget(folder, "Test Commander") == target, "Existing deck metadata name was not found.");
            MustFail(delegate { DeckWriter.Save(folder, target, "changed", false); }, "Existing deck was overwritten without authorization.");
            string backup = DeckWriter.Save(folder, target, "changed", true);
            Assert(File.ReadAllText(target) == "changed" && File.ReadAllText(backup) == content, "Atomic replacement / backup preservation failed.");
            Assert(!Directory.GetFiles(folder, "*.tmp").Any(), "Temporary save file was not cleaned up.");
            string fixtureForge = Path.Combine(directory, "forge");
            Directory.CreateDirectory(fixtureForge);
            File.WriteAllText(Path.Combine(fixtureForge, "forge.profile.properties"), "userDir=" + Path.Combine(directory, "profile-user").Replace('\\', '/') + "\r\ndecksDir=\r\n");
            Assert(ForgePaths.CommanderFolder(fixtureForge) == Path.Combine(directory, "profile-user", "decks", "commander"), "Portable profile userDir was ignored.");
            File.WriteAllText(Path.Combine(fixtureForge, "forge.profile.properties"), "userDir=ignored\r\ndecksDir=" + Path.Combine(directory, "custom-decks").Replace('\\', '/') + "\r\n");
            Assert(ForgePaths.CommanderFolder(fixtureForge) == Path.Combine(directory, "custom-decks", "commander"), "Custom decksDir was ignored.");
            return "PASS: import formats, face aliases, alternate commander metadata and eligibility, 100-card preservation, missing/repeated/primary alternate rejection, Commander serialization, safe saves, backups, and portable profiles.";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--self-test")
            {
                string report = Path.GetFullPath(args[1]);
                try { string result = SelfTests.Run(Path.Combine(Path.GetDirectoryName(report), "self-test-" + Guid.NewGuid().ToString("N"))); File.WriteAllText(report, result); return 0; }
                catch (Exception error) { File.WriteAllText(report, "FAIL: " + error); return 1; }
            }
            if (args.Length >= 3 && args[0] == "--validate")
            {
                string report = Path.GetFullPath(args[2]);
                try
                {
                    string forge = args.Length >= 4 ? args[3] : ForgePaths.Discover();
                    Catalog catalog = Catalog.Load(forge);
                    string text = File.ReadAllText(args[1]);
                    List<CardEntry> parsed = DeckParser.Parse(text);
                    string chosenCommander = args.Length >= 5 ? args[4] : parsed[0].Name;
                    List<CardEntry> checkedDeck = DeckParser.Validate(text, chosenCommander, catalog);
                    CardInfo chosenCard;
                    catalog.TryResolve(chosenCommander, out chosenCard);
                    File.WriteAllText(report, "PASS: " + checkedDeck.Sum(e => e.Quantity) + " cards; " + checkedDeck.Count + " names; all supported; commander=" + chosenCard.Name + "; Commander folder=" + ForgePaths.CommanderFolder(forge));
                    return 0;
                }
                catch (Exception error) { File.WriteAllText(report, "FAIL: " + error.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs eventArgs) { MessageBox.Show(eventArgs.Exception.Message, "Forge Deck Importer", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
            Application.Run(new ImporterForm());
            return 0;
        }
    }
}
