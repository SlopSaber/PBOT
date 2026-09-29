using BGLib.Polyglot;
using CountersPlus.Custom;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace PBOT.Managers;

internal sealed class PBOTCounterPreview : ICounterPreview
{
    private readonly Config _config;

    public PBOTCounterPreview(Config config)
    {
        _config = config;
    }

    public void Render(CounterPreviewContext preview)
    {
        TMP_Text text = preview.CreateText();
        text.fontSize = 2.75f;
        CultureInfo culture = Localization.Instance?.SelectedCultureInfo ?? CultureInfo.CurrentCulture;
        text.text = _config.ShowDifference
            ? string.Format(culture, "+{0:P" + _config.Precision + "}", 0.0035)
            : string.Format(culture, "{0:P" + _config.Precision + "}", 0.9624);

        if (ColorUtility.TryParseHtmlString(_config.ShowDifference ? _config.BeatingFrameColor : _config.DefaultColor, out Color color))
            text.color = color;
    }
}
