/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Simitone.Client.UI.Panels.EODs
{
    /// <summary>
    /// The control a <see cref="EODLayoutElement"/> asks for, which is also the
    /// <c>.uis</c> node that creates it.
    /// </summary>
    /// <remarks>
    /// The mapping is not a free choice - it is fixed by
    /// <c>UIScript.FUNCTIONS</c>, the list of node names the parser will actually invoke a
    /// method for. A kind whose name is not in that list produces a layout that parses
    /// cleanly and then does nothing, so the two have to agree and
    /// <c>EODLayoutBuilderTests</c> checks the emitted text against the list.
    /// </remarks>
    public enum EODControlKind
    {
        /// <summary><c>AddButton</c>. A push button, auto-wired to a property by name.</summary>
        Button,

        /// <summary>
        /// <c>AddButton</c> against a property typed as a radio button, which is how the
        /// dresser panel's five one-of-N toggles are built. Same node as
        /// <see cref="Button"/>; the difference is only which class the parser instantiates,
        /// and it takes that from the property's declared type.
        /// </summary>
        RadioButton,

        /// <summary><c>AddText</c>, which is a plain <c>UILabel</c>.</summary>
        Label,

        /// <summary>
        /// <c>AddTextEdit</c>. Deliberately an <em>add</em> and not a
        /// <c>UIScript.Create&lt;UITextEdit&gt;</c> the panel makes itself: several panels call
        /// <c>InitDefaultSlider</c> on a text edit, and that method does
        /// <c>Parent.Add(...)</c>, so the control has to already be in the container.
        /// </summary>
        TextEdit,

        /// <summary><c>AddListBox</c>, for the same reason as <see cref="TextEdit"/>.</summary>
        ListBox,

        /// <summary><c>AddSlider</c>.</summary>
        Slider,

        /// <summary><c>AddProgressBar</c>.</summary>
        ProgressBar,

        /// <summary>
        /// <c>SetControlProperties</c> only - which is the only way an image can be declared,
        /// because the parser has no <c>AddImage</c>. The panel constructs the image itself
        /// with <c>UIScript.Create&lt;UIImage&gt;</c> and this node is what gives it a
        /// position, so the node's id is the id the panel asks for.
        /// </summary>
        Image,

        /// <summary>
        /// Also <c>SetControlProperties</c>, but for a control the panel builds out of
        /// something FSO.UI has no node for at all - a card hand, a masked container, a
        /// catalog viewer. Those only ever need a position, and a node that tried to create
        /// one would either not compile against the right constructor or would be ignored.
        /// </summary>
        PanelOwned,
    }

    /// <summary>
    /// One control in a Simitone-authored EOD layout: what it is called, what it is, and
    /// optionally where it goes and what it says.
    /// </summary>
    public sealed class EODLayoutElement
    {
        /// <summary>
        /// The name the panel knows the control by. This has to be the panel's own property
        /// name, because that is what <c>UIScript</c> wires the created control up to; for
        /// <see cref="EODControlKind.PanelOwned"/> and <see cref="EODControlKind.Image"/> it is
        /// the id the panel passes to <c>UIScript.Create</c>, which is not always the same
        /// string as the property it is stored in.
        /// </summary>
        public string Id { get; }

        public EODControlKind Kind { get; }

        /// <summary>
        /// Text for a button's caption, or null for a control that shows something else
        /// (a label the panel writes into, a text edit the player types into, a picture).
        /// A caption becomes a <c>DefineString</c> in Simitone's own string table, so it is
        /// translatable - see <see cref="EODLayoutStrings"/>.
        /// </summary>
        public string Caption { get; }

        /// <summary>Width in pixels, or null for the kind's default.</summary>
        public int? Width { get; }

        /// <summary>Height in pixels, or null for the kind's default.</summary>
        public int? Height { get; }

        /// <summary>Forced grid row, or null to flow in declaration order.</summary>
        public int? Row { get; }

        /// <summary>Forced grid column, or null to flow in declaration order.</summary>
        public int? Column { get; }

        public EODLayoutElement(string id, EODControlKind kind, string caption = null,
            int? width = null, int? height = null, int? row = null, int? column = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a layout element needs an id", nameof(id));
            Id = id;
            Kind = kind;
            Caption = caption;
            Width = width;
            Height = height;
            Row = row;
            Column = column;
        }

        /// <summary>The node name this element compiles to, or null for a declaration-only one.</summary>
        public string NodeName
        {
            get
            {
                switch (Kind)
                {
                    case EODControlKind.Button:
                    case EODControlKind.RadioButton:
                        return "AddButton";
                    case EODControlKind.Label:
                        return "AddText";
                    case EODControlKind.TextEdit:
                        return "AddTextEdit";
                    case EODControlKind.ListBox:
                        return "AddListBox";
                    case EODControlKind.Slider:
                        return "AddSlider";
                    case EODControlKind.ProgressBar:
                        return "AddProgressBar";
                    default:
                        return "SetControlProperties";
                }
            }
        }
    }

    /// <summary>
    /// A whole Simitone-authored EOD panel layout: the file it is written to, the panel it is
    /// for, and the controls it has to contain.
    /// </summary>
    /// <remarks>
    /// A <c>.uis</c> is an absolute layout, but it is not a window: it says "this button is at
    /// 137, 402" and nothing about how big the thing it draws into is. The grid is therefore
    /// also how the panel's size is decided, and the size is what
    /// <see cref="EODPanelFrame"/> centres, so it is part of the spec rather than something
    /// inferred at run time.
    /// </remarks>
    public sealed class EODLayoutSpec
    {
        /// <summary>File name, e.g. "wargameeod.uis". The name the panel's RenderScript asks for.</summary>
        public string FileName { get; }

        /// <summary>What this panel is, for the log line and the generated file's header.</summary>
        public string Title { get; }

        /// <summary>How many controls go in a row before the next row starts.</summary>
        public int Columns { get; }

        /// <summary>Width of one grid cell, including the gap after it.</summary>
        public int CellWidth { get; }

        /// <summary>Height of one grid row, including the gap after it.</summary>
        public int RowHeight { get; }

        /// <summary>Gap between the panel's edge and its first control, and between controls.</summary>
        public int Margin { get; }

        public IReadOnlyList<EODLayoutElement> Elements { get; }

        /// <summary>
        /// The panel's <c>Texture2D</c> properties, by name. A texture can only reach a panel
        /// through a <c>DefineImage</c> node, and the only kind of <c>DefineImage</c> that is
        /// not an EA artwork reference is one that names a picture of ours - so these are all
        /// bound to <see cref="EODLayoutBuilder.PlaceholderImage"/>.
        /// </summary>
        public IReadOnlyList<string> TextureNames { get; }

        public EODLayoutSpec(string fileName, string title, IEnumerable<EODLayoutElement> elements,
            IEnumerable<string> textureNames = null, int columns = 4, int cellWidth = 104,
            int rowHeight = 30, int margin = 10)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("a layout needs a file name", nameof(fileName));
            FileName = fileName;
            Title = title ?? fileName;
            Elements = (elements ?? Enumerable.Empty<EODLayoutElement>()).ToList();
            TextureNames = (textureNames ?? Enumerable.Empty<string>()).ToList();
            Columns = Math.Max(1, columns);
            CellWidth = cellWidth;
            RowHeight = rowHeight;
            Margin = margin;
        }

        /// <summary>The panel's own id, lower case, as <c>UIScript</c> stores string keys.</summary>
        public string StringId { get { return FileName.Replace(".uis", string.Empty).ToLowerInvariant(); } }
    }

    /// <summary>
    /// Turns an <see cref="EODLayoutSpec"/> into the text of a <c>.uis</c> file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of path (b)/(c), and it is deliberately a text generator rather than
    /// a control factory.</b> The obvious alternative - building the controls in code at the
    /// moment the panel asks for its layout - is not available, because every panel in
    /// <c>FSO.Client.UI.Panels.EODs</c> asks for its layout by calling
    /// <c>UIContainer.RenderScript(name)</c>, and that opens a file by name. A file is what the
    /// panels need, so a file is what this produces: authored here, by us, from a declaration
    /// of which named control each panel has to find.
    /// </para>
    /// <para>
    /// It is pure - no IO, no graphics device, no content directory, no reflection over
    /// FSO.Client - so the whole set of layouts is checkable in
    /// <c>Simitone.Tests.UI.EODLayoutBuilderTests</c>, including the one check that matters
    /// most: that the committed <c>.uis</c> files still say what this says they say.
    /// </para>
    /// <para>
    /// <b>Nothing here is EA's.</b> There is no <c>DefineImage</c> with a
    /// <c>0x</c>-prefixed asset id anywhere in the output, because those name artwork out of a
    /// The Sims Online install. The only images are
    /// <see cref="PlaceholderImage"/>, a flat tile this port draws itself, used where a panel
    /// insists on having a texture object at all.
    /// </para>
    /// </remarks>
    public static class EODLayoutBuilder
    {
        /// <summary>
        /// The one picture a generated layout is allowed to name, relative to the content
        /// directory, which is what <c>UIElement.GetTexture(string)</c> resolves through
        /// <c>Content.Get().CustomUI</c> (its provider matches on <c>uigraphics/....png</c>).
        /// </summary>
        public const string PlaceholderImage = "uigraphics/eod/eod_placeholder.png";

        /// <summary>Prefix on a <c>DefineString</c> id, so it can never collide with a control's.</summary>
        public const string StringIdPrefix = "str_";

        /// <summary>Prefix on a <c>DefineImage</c> id, for the same reason.</summary>
        public const string TextureIdPrefix = "img_";

        private const string StringDir = "UIText";
        private const int LabelFont = 10;
        private const int ListFont = 9;
        private const int ListRowHeight = 14;
        private const int SliderHeight = 12;
        private const int ProgressHeight = 10;

        /// <summary>Default control size per kind, used when the element does not give one.</summary>
        public static void DefaultSize(EODControlKind kind, out int width, out int height)
        {
            switch (kind)
            {
                case EODControlKind.Button:
                case EODControlKind.RadioButton:
                case EODControlKind.PanelOwned:
                    width = 92; height = 20; break;
                case EODControlKind.Label:
                    width = 92; height = 14; break;
                case EODControlKind.TextEdit:
                    width = 92; height = 18; break;
                case EODControlKind.ListBox:
                    width = 92; height = 72; break;
                case EODControlKind.Slider:
                    width = 92; height = SliderHeight; break;
                case EODControlKind.ProgressBar:
                    width = 92; height = ProgressHeight; break;
                default: // Image
                    width = 24; height = 24; break;
            }
        }

        /// <summary>How many grid rows a spec's elements occupy.</summary>
        public static int RowCount(EODLayoutSpec spec)
        {
            var rows = 0;
            for (var i = 0; i < spec.Elements.Count; i++)
            {
                var row = spec.Elements[i].Row ?? (i / spec.Columns);
                if (row + 1 > rows) rows = row + 1;
            }
            return Math.Max(1, rows);
        }

        /// <summary>
        /// The size of the panel this layout draws into, which nothing in the <c>.uis</c> says
        /// and which <see cref="EODPanelFrame"/> needs in order to centre it.
        /// </summary>
        /// <remarks>
        /// The furthest corner any control actually reaches, not the nominal grid - a list box is
        /// taller than a row, so a panel sized from the grid alone would have its own list box
        /// hanging off the bottom.
        /// </remarks>
        public static Microsoft.Xna.Framework.Vector2 Size(EODLayoutSpec spec)
        {
            var right = 0f;
            var bottom = 0f;
            for (var i = 0; i < spec.Elements.Count; i++)
            {
                var element = spec.Elements[i];
                var position = Position(spec, element, i);
                int w, h;
                DefaultSize(element.Kind, out w, out h);
                if (element.Width.HasValue) w = element.Width.Value;
                if (element.Height.HasValue) h = element.Height.Value;
                right = Math.Max(right, position.X + w);
                bottom = Math.Max(bottom, position.Y + h);
            }
            return new Microsoft.Xna.Framework.Vector2(
                Math.Max(right + spec.Margin, spec.Margin * 2),
                Math.Max(bottom + spec.Margin, spec.Margin * 2));
        }

        /// <summary>
        /// Where one element goes. Fixed by declaration order unless the element pins a row or
        /// a column, so the same spec always produces the same file.
        /// </summary>
        public static Microsoft.Xna.Framework.Vector2 Position(EODLayoutSpec spec, EODLayoutElement element, int index)
        {
            var row = element.Row ?? (index / spec.Columns);
            var col = element.Column ?? (index % spec.Columns);
            return new Microsoft.Xna.Framework.Vector2(
                spec.Margin + col * spec.CellWidth,
                spec.Margin + row * spec.RowHeight);
        }

        /// <summary>The whole file, as text. Deterministic, so it can be diffed and committed.</summary>
        public static string Build(EODLayoutSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            var sb = new StringBuilder();
            sb.Append(Header(spec));
            sb.Append("<Begin>\r\n");

            AppendStrings(sb, spec);
            AppendTextures(sb, spec);

            for (var i = 0; i < spec.Elements.Count; i++)
            {
                AppendElement(sb, spec, spec.Elements[i], i);
            }

            sb.Append("\r\n<End>\r\n");
            return sb.ToString();
        }

        private static string Header(EODLayoutSpec spec)
        {
            return
                "# " + spec.Title + " - Simitone EOD panel layout." + "\r\n" +
                "#\r\n" +
                "# Generated by Simitone.Client.UI.Panels.EODs.EODLayoutBuilder from the spec in\r\n" +
                "# EODLayouts.cs. Edit the spec, not this file: EODLayoutBuilderTests fails if the\r\n" +
                "# two disagree, so a hand edit here is a build failure rather than a silent drift.\r\n" +
                "#\r\n" +
                "# This is not an EA layout. The one the retail panel was written against is an\r\n" +
                "# artwork layout whose pictures live in a The Sims Online install, so the positions\r\n" +
                "# below are Simitone's own grid and the only picture any of these files names is\r\n" +
                "# the flat tile this port draws, " + PlaceholderImage + ".\r\n" +
                "#\r\n" +
                "# Captions come from Simitone's own string table (" + EODLayoutStrings.Table + "),\r\n" +
                "# so they are translatable rather than baked into this file.\r\n" +
                "#\r\n" +
                "# " + spec.Elements.Count + " controls, " + RowCount(spec) + " rows, " +
                spec.Columns + " columns.\r\n\r\n";
        }

        private static void AppendStrings(StringBuilder sb, EODLayoutSpec spec)
        {
            var captioned = spec.Elements
                .Where(x => !string.IsNullOrEmpty(x.Caption))
                .GroupBy(x => x.Caption, StringComparer.Ordinal)
                .ToList();
            if (captioned.Count == 0) return;

            sb.Append("    # Captions. " + EODLayoutStrings.Table + " is Simitone's own UI string table\r\n");
            sb.Append("    # (Content/UI/uitext/<lang>.dir/" + EODLayoutStrings.TableFileName + "), so these are\r\n");
            sb.Append("    # translatable rather than baked into this file.\r\n\r\n");
            sb.Append("    <Begin><SetSharedProperties stringDir = \"" + StringDir + "\" stringTable = " + EODLayoutStrings.Table + ">\r\n");
            foreach (var group in captioned)
            {
                var name = StringIdPrefix + Slug(group.Key);
                sb.Append("        <DefineString \"").Append(name).Append("\" stringIndex = ")
                  .Append(EODLayoutStrings.IdFor(group.Key).ToString(CultureInfo.InvariantCulture)).Append(">\r\n");
            }
            sb.Append("    <End>\r\n\r\n");
        }

        private static void AppendTextures(StringBuilder sb, EODLayoutSpec spec)
        {
            var needsScrollbar = spec.Elements.Any(x =>
                x.Kind == EODControlKind.TextEdit || x.Kind == EODControlKind.ListBox);
            var needsTrack = spec.Elements.Any(x => x.Kind == EODControlKind.Slider);
            if (spec.TextureNames.Count == 0 && !needsScrollbar && !needsTrack) return;

            sb.Append("    # Pictures. A Texture2D property can only be filled by a DefineImage node, and\r\n");
            sb.Append("    # the retail layouts fill them with TSO artwork ids. Every panel that has one\r\n");
            sb.Append("    # gets the same flat tile instead: enough for a control that asks a texture for its\r\n");
            sb.Append("    # size or its graphics device, and visibly a placeholder rather than a lie.\r\n\r\n");
            foreach (var texture in spec.TextureNames)
            {
                sb.Append("    <DefineImage \"").Append(TextureIdPrefix).Append(texture)
                  .Append("\" assetID=\"").Append(PlaceholderImage).Append("\">\r\n");
            }
            if (needsScrollbar)
            {
                sb.Append("    <DefineImage \"").Append(TextureIdPrefix).Append("Scrollbar")
                  .Append("\" assetID=\"").Append(PlaceholderImage).Append("\">\r\n");
            }
            if (needsTrack)
            {
                sb.Append("    <DefineImage \"").Append(TextureIdPrefix).Append("SliderTrack")
                  .Append("\" assetID=\"").Append(PlaceholderImage).Append("\">\r\n");
            }
            sb.Append("\r\n");
        }

        private static void AppendElement(StringBuilder sb, EODLayoutSpec spec, EODLayoutElement element, int index)
        {
            var pos = Position(spec, element, index);
            int w, h;
            DefaultSize(element.Kind, out w, out h);
            if (element.Width.HasValue) w = element.Width.Value;
            if (element.Height.HasValue) h = element.Height.Value;

            sb.Append("    <").Append(element.NodeName).Append(" \"").Append(element.Id).Append('"');
            sb.Append(" position=(")
              .Append(Math.Max(0, (int)pos.X).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Max(0, (int)pos.Y).ToString(CultureInfo.InvariantCulture)).Append(')');

            switch (element.Kind)
            {
                case EODControlKind.Button:
                case EODControlKind.RadioButton:
                case EODControlKind.PanelOwned:
                    // size first, then the caption: UIButton.Size only records a width, and
                    // setting the caption afterwards would re-measure and throw it away.
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    if (!string.IsNullOrEmpty(element.Caption))
                    {
                        sb.Append(" text=\"").Append(StringIdPrefix).Append(Slug(element.Caption)).Append('"');
                    }
                    break;

                case EODControlKind.Label:
                    sb.Append(" font=").Append(LabelFont);
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    if (!string.IsNullOrEmpty(element.Caption))
                    {
                        sb.Append(" text=\"").Append(StringIdPrefix).Append(Slug(element.Caption)).Append('"');
                    }
                    break;

                case EODControlKind.TextEdit:
                    sb.Append(" font=").Append(LabelFont);
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    sb.Append(" capacity=").Append(Math.Max(1, h * 4));
                    sb.Append(" scrollbarImage=\"").Append(TextureIdPrefix).Append("Scrollbar").Append('"');
                    break;

                case EODControlKind.ListBox:
                    sb.Append(" font=").Append(ListFont);
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    sb.Append(" rowHeight=").Append(ListRowHeight);
                    sb.Append(" scrollbarImage=\"").Append(TextureIdPrefix).Append("Scrollbar").Append('"');
                    break;

                case EODControlKind.Slider:
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    sb.Append(" image=\"").Append(TextureIdPrefix).Append("SliderTrack").Append('"');
                    sb.Append(" minValue=0 maxValue=10 orientation=0");
                    break;

                case EODControlKind.ProgressBar:
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    sb.Append(" minValue=0 maxValue=100");
                    break;

                default: // Image, PanelOwned
                    sb.Append(" size=(").Append(w).Append(',').Append(h).Append(')');
                    break;
            }

            sb.Append(">\r\n");
        }

        /// <summary>
        /// A caption reduced to something the <c>.uis</c> grammar will accept as an id: letters,
        /// digits and underscores only, non-empty, and stable for a given caption so the
        /// <c>DefineString</c> and the <c>text</c> that points at it cannot drift apart.
        /// </summary>
        /// <remarks>
        /// A caption with no letters or digits in it at all - the scoreboard's "+" and "-", the
        /// door's ">" - has nothing to slug from, and two such captions must not collapse onto
        /// the same id or one button would show the other's word. Those get a short hash of the
        /// caption instead, computed with a fixed algorithm because <see cref="string.GetHashCode()"/>
        /// is randomised per process and would make the committed files differ between machines.
        /// </remarks>
        public static string Slug(string caption)
        {
            if (string.IsNullOrEmpty(caption)) return "x";
            var sb = new StringBuilder(caption.Length);
            foreach (var c in caption)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            }
            var slug = sb.ToString().Trim('_');
            if (slug.Length == 0)
            {
                //Deterministic, unlike GetHashCode, which is randomised per process on .NET
                //Core and would make the committed .uis files differ between machines.
                unchecked
                {
                    uint hash = 2166136261;
                    foreach (var c in caption) { hash ^= c; hash *= 16777619; }
                    return "c" + hash.ToString("x8", CultureInfo.InvariantCulture);
                }
            }
            //The grammar's id character set does not include a leading-digit rule, but a bare
            //number would collide with the numeric attribute values, so keep it word-like.
            return char.IsDigit(slug[0]) ? "n" + slug : slug;
        }
    }
}
