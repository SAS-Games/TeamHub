# Validation

Generated output is strictly parsed into canonical Flow Designer nodes and connections. Connector pin numbers are mapped to the existing `output_N` and `input_N` identifiers, notes use the existing `customProperties.notes` field, and layout uses the existing `top-bottom` port layout. The existing `IFlowValidator` remains authoritative for graph validity.

Unknown node types, invalid pins, unknown evidence, invalid endpoints, malformed JSON, and domain errors reject the entire proposal. Nothing is partially saved or published.