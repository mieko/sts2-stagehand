using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace Stagehand.Sts2;

internal static class StagehandSettingsPanel
{
    private const int CloneScripts = (int)Node.DuplicateFlags.Scripts;
    private static readonly FieldInfo? TabsField = typeof(NSettingsTabManager)
        .GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Install(SceneTree tree, Func<ConnectionSettings> read,
        Func<ConnectionSettings, string> apply, Func<(string Summary, bool Available)> connection, Func<string> copy)
    {
        tree.NodeAdded += node =>
        {
            if (node is not NSettingsTabManager manager)
                return;

            manager.Ready += () =>
            {
                try { AddTab(manager, read, apply, connection, copy); }
                catch (Exception error) { GD.PushWarning("Could not add Stagehand settings: " + error.Message); }
            };
        };
    }

    private static void AddTab(NSettingsTabManager manager, Func<ConnectionSettings> read,
        Func<ConnectionSettings, string> apply, Func<(string Summary, bool Available)> connection, Func<string> copy)
    {
        if (TabsField?.GetValue(manager) is not Dictionary<NSettingsTab, NSettingsPanel> tabs || tabs.Count == 0)
            throw new InvalidOperationException("The game settings tab layout is unavailable");

        var (originalTab, originalPanel) = tabs.First();
        var tickbox = GD.Load<PackedScene>("res://scenes/screens/settings_tickbox.tscn");
        var nativeToggle = Descendants(originalPanel.Content).OfType<NTickbox>().First();
        var row = (Control)nativeToggle.GetParent();
        var tickboxScript = GD.Load<Script>("res://src/Core/Nodes/CommonUi/NTickbox.cs");
        var button = Descendants(originalPanel).OfType<NOpenModdingScreenButton>().Single();
        var divider = originalPanel.Content.GetChildren().OfType<ColorRect>().First();

        var panel = (NSettingsPanel)originalPanel.Duplicate(CloneScripts);
        panel.Name = "StagehandPanel";
        panel.Hide();
        var content = panel.GetNode<VBoxContainer>(originalPanel.GetPathTo(originalPanel.Content));
        foreach (var child in panel.GetChildren())
            if (child != content)
                child.Free();

        foreach (var child in content.GetChildren())
            child.Free();

        BuildForm(panel, content,
            () => Attach<NTickbox>(tickbox.Instantiate<Control>(), tickboxScript),
            text => CreateButton(button, text), CreateTokenEditor(button.CustomMinimumSize.X),
            row, divider, read, apply, connection, copy);
        originalPanel.GetParent().AddChild(panel);

        var tab = (NSettingsTab)originalTab.Duplicate(CloneScripts);
        tab.Name = "Stagehand";
        if (tab.GetNodeOrNull<TextureRect>("TabImage")?.Material is ShaderMaterial material)
            tab.GetNode<TextureRect>("TabImage").Material = (ShaderMaterial)material.Duplicate();

        manager.AddChild(tab);
        tab.SetLabel("Stagehand");
        tab.Deselect();
        PlaceTab(manager, tabs.Keys.ToArray(), tab);
        tabs.Add(tab, panel);
        tab.Released += _ => manager.Call("SwitchTabTo", tab);
    }

    private static void PlaceTab(Control manager, NSettingsTab[] tabs, NSettingsTab added)
    {
        var first = tabs[0];
        var right = tabs[^1].Position.X + first.Size.X;
        var spacing = tabs.Length > 1 ? tabs[1].Position.X - first.Position.X : first.Size.X;
        added.Position = new Vector2(tabs[^1].Position.X + spacing, first.Position.Y);
        if (added.Position.X + first.Size.X <= manager.Size.X || manager.Size.X <= 0)
            return;

        // Keep the original row's occupied span when an extra tab would overflow.
        var width = (right - first.Position.X) / (tabs.Length + 1);
        var left = first.Position.X;
        foreach (var tab in tabs.Append(added))
        {
            tab.Position = new Vector2(left, first.Position.Y);
            tab.Size = new Vector2(width, tab.Size.Y);
            left += width;
        }
    }

    private static void BuildForm(NSettingsPanel panel, VBoxContainer content, Func<NTickbox> createTickbox,
        Func<string, NSettingsButton> createButton, LineEdit accessToken, Control template, ColorRect divider,
        Func<ConnectionSettings> read, Func<ConnectionSettings, string> apply,
        Func<(string Summary, bool Available)> connection, Func<string> copy)
    {
        var enabled = AddToggle("Enable Stagehand");
        var remote = AddToggle("Allow Remote Connections");
        var token = AddToggle("Require Access Token");
        AddRow("Access Token", accessToken);

        var copyButton = createButton("Copy");
        AddRow("Connection Details", copyButton);
        var current = AddRow("");

        var developer = AddToggle("Developer Mode", Colors.Khaki);
        var warning = AddRow("When enabled, clients can execute arbitrary code on this computer");

        var applyButton = createButton("Apply");
        AddRow("", applyButton);
        applyButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        var applyRow = (MarginContainer)applyButton.GetParent();
        applyRow.AddThemeConstantOverride("margin_top", applyRow.GetThemeConstant("margin_top") + 40);
        applyRow.AddThemeConstantOverride("margin_bottom", applyRow.GetThemeConstant("margin_bottom") + 12);

        const string applyHint = "Applying changes may disconnect clients.";
        var status = AddRow(applyHint);
        status.HorizontalAlignment = HorizontalAlignment.Center;
        var localToken = false;

        panel.VisibilityChanged += () =>
        {
            if (!panel.IsVisibleInTree())
                return;

            // The native settings screen sizes its labels when it opens.
            var labelSize = template.GetNode<RichTextLabel>("Label").GetThemeFontSize("normal_font_size");
            foreach (var row in content.GetChildren().OfType<MarginContainer>())
            {
                var label = row.GetNode<RichTextLabel>("Label");
                var size = label == warning || label == status || label == current ? labelSize * 3 / 4 : labelSize;
                label.AddThemeFontSizeOverride("normal_font_size", size);
            }

            var settings = read();
            enabled.IsTicked = settings.Enabled;
            remote.IsTicked = settings.AllowRemoteConnections;
            token.IsTicked = settings.RequireAccessToken;
            localToken = settings.RequireAccessToken;
            accessToken.Text = settings.AccessToken ?? "";
            developer.IsTicked = settings.DeveloperMode;
            RefreshTokenControls();
            RefreshConnection();
            status.Text = applyHint;
        };

        remote.Toggled += _ =>
        {
            if (!remote.IsTicked)
                token.IsTicked = localToken;

            RefreshTokenControls();
        };
        token.Toggled += _ =>
        {
            if (!remote.IsTicked)
                localToken = token.IsTicked;

            RefreshTokenControls();
        };
        applyButton.Released += _ =>
        {
            status.Text = apply(ReadDraft(read()));
            RefreshConnection();
        };
        copyButton.Released += _ =>
        {
            var saved = read();
            status.Text = copy();
            if (ReadDraft(saved) != saved)
                status.Text += " Select Apply to use your edits.";
        };

        ConnectionSettings ReadDraft(ConnectionSettings saved) => saved with
        {
            Enabled = enabled.IsTicked,
            AllowRemoteConnections = remote.IsTicked,
            RequireAccessToken = remote.IsTicked || token.IsTicked,
            AccessToken = accessToken.Text.Length == 0 ? null : accessToken.Text,
            DeveloperMode = developer.IsTicked
        };

        void RefreshTokenControls()
        {
            if (remote.IsTicked)
            {
                token.IsTicked = true;
                token.Disable();
            }
            else
                token.Enable();

            accessToken.Editable = token.IsTicked;
            accessToken.PlaceholderText = token.IsTicked ? "Use generated token" : "No token required";
        }

        void RefreshConnection()
        {
            var (summary, available) = connection();
            current.Text = summary;
            if (available)
                copyButton.Enable();
            else
                copyButton.Disable();
        }

        NTickbox AddToggle(string text, Color? color = null)
        {
            var toggle = createTickbox();
            var label = AddRow(text, toggle);
            if (color is { } tint)
                label.AddThemeColorOverride("default_color", tint);

            return toggle;
        }

        RichTextLabel AddRow(string text, Control? control = null)
        {
            if (control != null && content.GetChildCount() > 0)
                content.AddChild(divider.Duplicate(0));

            var row = (MarginContainer)template.Duplicate(0);
            var label = row.GetNode<RichTextLabel>("Label");
            foreach (var child in row.GetChildren())
                if (child != label)
                    child.Free();

            label.BbcodeEnabled = false;
            label.Text = text;
            if (control != null)
            {
                control.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
                row.AddChild(control);
            }
            else
            {
                row.CustomMinimumSize = Vector2.Zero;
                label.FitContent = true;
                label.ScrollActive = false;
            }

            content.AddChild(row);
            return label;
        }
    }

    private static LineEdit CreateTokenEditor(float width)
    {
        var screen = GD.Load<PackedScene>("res://scenes/screens/custom_run/custom_run_screen.tscn").Instantiate();
        try
        {
            var editor = (LineEdit)screen.FindChild("SeedInput", true, false).Duplicate(CloneScripts);
            editor.Name = "AccessToken";
            editor.CustomMinimumSize = new Vector2(width, editor.CustomMinimumSize.Y);
            editor.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            editor.Secret = false;
            return editor;
        }
        finally { screen.Free(); }
    }

    private static NSettingsButton CreateButton(NSettingsButton template, string text)
    {
        // The settings screen connects the original Modding action. Clone just the control.
        var button = (NSettingsButton)template.Duplicate(CloneScripts);
        button.Show();
        button.Ready += () =>
        {
            button.Enable();
            button.GetNode<Label>("Label").Text = text;
        };
        return button;
    }

    private static T Attach<T>(Control control, Variant script) where T : Control
    {
        var id = control.GetInstanceId();
        control.SetScript(script);
        return GodotObject.InstanceFromId(id) as T
            ?? throw new InvalidOperationException($"The game could not create {typeof(T).Name}");
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
