# Selected UI XAML baseline candidates

This is the only active Verify.Xaml directory for ticket 11's rebuilt UI. It deliberately
contains no approved `*.verified.xml` or `*.verified.png` files until all of these gates pass:

1. the real 1440x900 preview is explicitly accepted as matching the selected UI;
2. the calibrated desktop probe reports 1920x1080, 96 DPI, light app theme, `zh-CN`
   culture/UI culture, the required fonts, and WPF `SoftwareOnly` rendering;
3. all 19 received XAML/PNG pairs are byte-identical for ten consecutive runs;
4. each before/after/diff proposal is reviewed without global tolerance or broad masks.

The rejected pre-reset approved files remain one directory above as historical evidence.
Neither the runner nor Verify.Xaml reads them.
