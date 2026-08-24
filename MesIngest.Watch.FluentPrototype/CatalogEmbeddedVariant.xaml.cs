using System.Windows;

namespace MesIngest.Watch.FluentPrototype;

public partial class CatalogEmbeddedVariant
{
    public CatalogEmbeddedVariant() => InitializeComponent();

    public void SelectAreaVariant(string variant) => AreaFilterPage.SelectVariant(variant);

    public void SelectAreaLiveVariant(string variant) => AreaLivePage.SelectVariant(variant);

    public void ApplyAreaLiveScenario(string scenario) => AreaLivePage.ApplyScenario(scenario);

    public void SelectErrorVariant(string variant) => ErrorSearchPage.SelectVariant(variant);

    public void SelectOverviewVariant(string variant) => OverviewPage.SelectVariant(variant);

    public void SetErrorReviewSwitcherVisible(bool isVisible) => ErrorSearchPage.SetReviewSwitcherVisible(isVisible);

    public void SetOverviewReviewSwitcherVisible(bool isVisible) => OverviewPage.SetReviewSwitcherVisible(isVisible);

    public void ApplyOverviewScenario(string scenario) => OverviewPage.ApplyScenario(scenario);

    public void SetNavigationPane(bool isOpen) => Navigation.IsPaneOpen = isOpen;

    public void Navigate(string page)
    {
        var normalized = page.Trim().ToLowerInvariant();
        if (normalized is "overview" or "home")
        {
            Show(OverviewPage, OverviewNav);
        }
        else if (normalized is "audit" or "readability" or "catalog")
        {
            Show(AuditPage, AuditNav);
        }
        else if (normalized is "area-live" or "arealive" or "live")
        {
            Show(AreaLivePage, AreaFilterNav);
        }
        else if (normalized is "area" or "areas" or "area-filter" or "filters")
        {
            Show(AreaFilterPage, AreaFilterNav);
        }
        else if (normalized is "errors" or "error" or "error-search" or "issues")
        {
            Show(ErrorSearchPage, ErrorSearchNav);
        }
        else
        {
            Show(SeriesPage, SeriesNav);
        }
    }

    private void Show(UIElement page, Wpf.Ui.Controls.NavigationViewItem nav)
    {
        OverviewPage.Visibility = ReferenceEquals(page, OverviewPage) ? Visibility.Visible : Visibility.Collapsed;
        SeriesPage.Visibility = ReferenceEquals(page, SeriesPage) ? Visibility.Visible : Visibility.Collapsed;
        AuditPage.Visibility = ReferenceEquals(page, AuditPage) ? Visibility.Visible : Visibility.Collapsed;
        ErrorSearchPage.Visibility = ReferenceEquals(page, ErrorSearchPage) ? Visibility.Visible : Visibility.Collapsed;
        AreaFilterPage.Visibility = ReferenceEquals(page, AreaFilterPage) ? Visibility.Visible : Visibility.Collapsed;
        AreaLivePage.Visibility = ReferenceEquals(page, AreaLivePage) ? Visibility.Visible : Visibility.Collapsed;
        OverviewNav.IsActive = ReferenceEquals(nav, OverviewNav);
        SeriesNav.IsActive = ReferenceEquals(nav, SeriesNav);
        AuditNav.IsActive = ReferenceEquals(nav, AuditNav);
        ErrorSearchNav.IsActive = ReferenceEquals(nav, ErrorSearchNav);
        AreaFilterNav.IsActive = ReferenceEquals(nav, AreaFilterNav);
    }

    private void OnOverviewClick(object sender, RoutedEventArgs e) => Navigate("overview");
    private void OnSeriesClick(object sender, RoutedEventArgs e) => Navigate("series");
    private void OnAuditClick(object sender, RoutedEventArgs e) => Navigate("audit");
    private void OnErrorSearchClick(object sender, RoutedEventArgs e) => Navigate("errors");
    private void OnAreaFilterClick(object sender, RoutedEventArgs e) => Navigate("area");
}
