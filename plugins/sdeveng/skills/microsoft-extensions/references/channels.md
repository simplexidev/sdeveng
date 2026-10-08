# Bounded channel concurrency

Prefer `Channel.CreateBounded<T>` with an explicit capacity and full mode when producers can outpace consumers. Choose whether writers wait or drop according to the data contract; use an unbounded channel only when growth is independently bounded and justified.

Pass cancellation tokens through reads, writes, and the work they trigger. Complete the writer when production ends, and observe reader completion so faults and shutdown are not lost. Define who owns completion when multiple producers share a writer.

References: [System.Threading.Channels](https://learn.microsoft.com/dotnet/core/extensions/channels).
