namespace MesIngest.Tests;

// WPF type loading is not thread safe across STA threads: two classes parsing BAML
// at the same time race inside System.Windows.Baml2006 known-member tables, and two
// windows initializing at once race inside WindowChromeWorker. Both surface as
// unrelated-looking failures in whichever class lost. Every test class that starts an
// STA thread and touches WPF joins this collection so they never overlap.
[CollectionDefinition("WpfDesktop", DisableParallelization = true)]
public sealed class WpfDesktopCollectionDefinition;
