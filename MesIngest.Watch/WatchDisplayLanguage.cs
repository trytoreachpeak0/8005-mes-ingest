namespace MesIngest.Watch;

internal enum WatchDisplayLanguage
{
    SimplifiedChinese,
    English,
}

internal sealed record WatchDisplayLanguageChoice(
    WatchDisplayLanguage Language,
    string Label);

internal sealed class WatchDisplayLanguageState
{
    internal WatchDisplayLanguageState(WatchDisplayLanguage initialLanguage)
    {
        Current = initialLanguage;
        Catalog = WatchTextCatalog.For(initialLanguage);
    }

    internal WatchDisplayLanguage Current { get; private set; }

    internal WatchTextCatalog Catalog { get; private set; }

    internal event EventHandler? Changed;

    internal void ApplyCommitted(WatchDisplayLanguage language)
    {
        if (language == Current)
        {
            return;
        }

        Current = language;
        Catalog = WatchTextCatalog.For(language);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

