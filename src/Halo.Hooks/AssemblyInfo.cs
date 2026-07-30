using System.Runtime.CompilerServices;

// ToolTarget's per-tool key picking is pure and worth pinning: it decides what the pill says about a tool
// call, and a wrong guess there is a line that reads as a fact. Same arrangement Halo.App already has.
[assembly: InternalsVisibleTo("Halo.Tests")]
