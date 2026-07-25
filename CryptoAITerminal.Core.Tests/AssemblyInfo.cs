using Xunit;

// ChatClient.ServerBaseUrl and ChatClient.LicenseTokenProvider are process-wide statics. The
// server-routing tests mutate them (restoring in a finally), and now that every AI feature gates on
// ChatClient.CanCallModel, thirteen other test classes READ them indirectly through UsesLiveModel /
// IsConfigured. With xunit's default per-collection parallelism those reads raced the writes:
// DailyBriefingServiceTests could observe a ServerBaseUrl set by ChatClientServerRoutingTests and
// see UsesLiveModel == true where it asserts false.
//
// Serialising the assembly removes the race at its source, which is better than sprinkling
// [Collection] attributes across thirteen files and hoping the next test that touches the statics
// remembers to join. The suite runs in about a second, so the parallelism bought nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
