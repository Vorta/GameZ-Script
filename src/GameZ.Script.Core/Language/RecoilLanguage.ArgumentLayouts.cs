using GameZ.Script.Core.Formats;

namespace GameZ.Script.Core.Language;

public static partial class RecoilLanguage
{
    private static bool BindUiArguments(LanguageDocument doc, LanguageRecord record, CancellationToken token)
    {
        var path = Ancestors(record.Parent);
        void Bind(ZrdNode node, LanguageArgument[] args) => BindArguments(doc, node, record.Key, args, token, fieldName: record.Key);
        void Rows(LanguageArgument[] args)
        {
            BindArguments(doc, record.Value!, record.Key, [new LanguageArgument("row", "a", Children: args)], token, repeat: true, fieldName: record.Key);
        }
        if (path.Length == 1)
        {
            switch (record.Key)
            {
                case "BACKGROUND_IMAGES": Rows(Roles("s i? i?", "image", "x (px)", "y (px)")); return true;
                case "BACKGROUND_TEXT": Rows(Roles("s i i i", "text", "x (px)", "y (px)", "font style")); return true;
                case "BACKGROUND_SOUNDS": Rows(Roles("s f?", "sample", "gain")); return true;
                case "BACKGROUND_VIDEOS":
                    Rows([A("video", "s"), A("x (px)", "i?"), A("y (px)", "i?"), Tuple("color", RGB())]); return true;
                case "FONTS":
                    foreach (var row in record.Values.Take(19).Where(n => n.Kind == ZrdKind.Array))
                    {
                        var color = row.Children.Count > 3 ? row.Children[3] : null;
                        var rgb = color?.Children.FirstOrDefault()?.Kind == ZrdKind.Array
                            ? Tuple("colors", Tuple("foreground", RGB()), Tuple("background", RGB())) : Tuple("color", RGB());
                        Bind(row, [A("style ID", "i"), A("font family", "s"), A("font size", "i"), rgb,
                            A("shadow", "i?"), A("weight", "i?"), A("alignment", "s?", choices: ["LEFT", "RIGHT", "CENTER"])]);
                    }
                    return true;
            }
        }
        if (IsWidgetContainer(path))
        {
            switch (record.Key)
            {
                case "TEXT":
                case "LABEL":
                    var args = Roles("s i i i", "text", "x (px)", "y (px)", "font style");
                    if (record.Values.FirstOrDefault()?.Kind == ZrdKind.Array) Rows(args);
                    else Bind(record.Value!, args);
                    return true;
                case "LIST_OFFSET":
                    Bind(record.Value!, [Tuple("visible list offset", Roles("i i", "x (px)", "y (px)")),
                        Tuple("overflow list offset", Roles("i i", "x (px)", "y (px)"))]); return true;
                case "SOUND": Bind(record.Value!, Roles("s f?", "sample", "gain")); return true;
                case "FILL_BITMAP": Bind(record.Value!, Roles("s i? i?", "image", "x (px)", "y (px)")); return true;
            }
        }
        return false;
    }

    private static bool BindHudArguments(LanguageDocument doc, LanguageRecord record, CancellationToken token)
    {
        LanguageArgument Rect(string label) => Tuple(label, Roles("i i i i", "left (px)", "top (px)", "right (px)", "bottom (px)"));
        LanguageArgument Point(string label) => Tuple(label, Roles("i i", "x (px)", "y (px)"));
        LanguageArgument Image(string label) => Tuple(label, A("image", "s"), A("x (px)", "i"), A("y (px)", "i"),
            A("visible", "s", "TRUE makes the image visible.", ["TRUE", "FALSE"], "image-flag"),
            A("centered", "s", "TRUE centers the image on the supplied position.", ["TRUE", "FALSE"], "image-flag"));
        LanguageArgument[]? args = record.Key switch
        {
            "TYPEI" => [Rect("viewport")],
            "TYPEII" => [Rect("viewport"), Image("image 1"), Image("image 2"), Image("image 3"),
                Tuple("alternate images", Roles("s s s s", "image 1", "image 2", "image 3", "image 4"))],
            "SENSOR" => [Image("sensor"), A(null, "f"), Point("sensor offset"),
                Tuple("sensor size", Roles("i i", "width (px)", "height (px)")), A("sensor range", "i"), Image("overlay"), Rect("meter")],
            "STATS" => [Point("counter"), Point("timer"),
                Tuple("list start", Roles("i i i", "x (px)", "y (px)", "row spacing (px)")),
                Tuple("list end", Roles("i i i", "x (px)", "y (px)", "row spacing (px)")),
                Point("position 5"), Point("position 6"), Point("position 7"), Point("position 8")],
            "NANITE" => [Rect("nanite"), Image("image 1"), Image("image 2"), Image("image 3")],
            "OBJECTIVE" => [A("phase duration (s)", "f"), Image("objective"), Rect("bar"),
                Tuple("bar color", RGB()), Point("summary offset"), Point("description offset"), Rect("meter"), Point("label")],
            "SHIELD" => [Image("shield"), Tuple("label", A("text", "x", "A stored zero is a null text pointer."), A("x (px)", "i"), A("y (px)", "i")), Rect("meter")],
            _ => null
        };
        if (args != null) { BindArguments(doc, record.Value!, record.Key, args, token, fieldName: record.Key); return true; }
        if (record.Key == "MODES")
        {
            BindArguments(doc, record.Value!, record.Key,
                [new LanguageArgument("mode", "a", Children: Roles("s s s i i", "image 1", "image 2", "image 3", "x (px)", "y (px)"))],
                token, repeat: true, count: 4, fieldName: record.Key);
            return true;
        }
        if (record.Key == "WEAPON")
        {
            BindArguments(doc, record.Value!, record.Key,
                [new LanguageArgument("weapon display", "a", Children: Roles("s s s s s s s i i", "image 1", "image 2", "image 3",
                    "image 4", "image 5", "side image 1", "side image 2", "x (px)", "y (px)"))], token, repeat: true, count: 9, fieldName: record.Key);
            return true;
        }
        return false;
    }
}
