using Luthn.Core.Classification;

namespace Luthn.Core.Policy;

public interface IPolicyEngine
{
    StorageDecision Decide(ClassificationResult classification);
}
