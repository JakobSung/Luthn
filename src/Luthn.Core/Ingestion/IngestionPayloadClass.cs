namespace Luthn.Core.Ingestion;

public enum IngestionPayloadClass
{
    RawSource,
    RedactedSummary,
    MetadataOnly,
    BinaryDigestOnly
}
