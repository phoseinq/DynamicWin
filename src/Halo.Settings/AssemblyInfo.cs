using System.Runtime.CompilerServices;

// Store is the panel's half of the settings contract, and the half that can lose a setting: it merges a
// draft onto the file the pill is also writing. That merge is worth pinning. Same arrangement Halo.App
// and Halo.Hooks already have.
[assembly: InternalsVisibleTo("Halo.Tests")]
