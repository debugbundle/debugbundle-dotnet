using System.Text;

namespace DebugBundle.Redaction;

public static partial class TelemetryPrivacy
{
    private static Dictionary<string, object?>? SanitizeScope(object? raw)
    {
        if (raw is not Dictionary<string, object?> map || map.Count != 2 || !map.TryGetValue("kind", out var kindValue) || kindValue is not string kind)
        {
            return null;
        }
        var field = kind == "page" ? "page_id" : kind == "operation" ? "operation_id" : null;
        if (field == null || !map.TryGetValue(field, out var idValue) || idValue is not string id || !EvidenceWire.FreshId(id))
        {
            return null;
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            [field] = id
        };
    }

    private static Dictionary<string, object?>? SanitizeEvidenceReferenceBlock(Dictionary<string, object?> map)
    {
        if (map.Count != 3 || !HasOnlyAllowedKeys(map, AllowedReferenceKeys) || !IsVersionOne(map))
        {
            return null;
        }
        if (!map.TryGetValue("failure_ref", out var failureValue) || failureValue is not string failureRef || !EvidenceWire.FreshId(failureRef))
        {
            return null;
        }
        if (!map.TryGetValue("scope", out var rawScope)) return null;
        var scope = SanitizeScope(rawScope);
        if (scope == null) return null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["failure_ref"] = failureRef,
            ["scope"] = scope
        };
    }

    private static Dictionary<string, object?>? SanitizeEvidenceLimitsBlock(Dictionary<string, object?> map)
    {
        if (!HasOnlyAllowedKeys(map, AllowedEvidenceLimitKeys) || !IsVersionOne(map))
        {
            return null;
        }
        var output = new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = 1 };
        foreach (var entry in map)
        {
            if (entry.Key == "version") continue;
            if (entry.Value is not string reason || !AllowedEvidenceLimitReasons.Contains(reason))
            {
                return null;
            }
            output[entry.Key] = reason;
        }
        return output;
    }

    private static Dictionary<string, object?>? SanitizeBrowserEventBlock(
        Dictionary<string, object?> raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        if (!HasOnlyAllowedKeys(raw, AllowedBrowserEventKeys)) return null;
        if (!raw.TryGetValue("kind", out var kindValue) || kindValue is not string kind || kind is not ("window_error" or "resource_error")) return null;
        if (!raw.TryGetValue("opaque", out var opaqueValue) || opaqueValue is not bool opaque) return null;
        if (!raw.TryGetValue("line_number", out var rawLine) || !raw.TryGetValue("column_number", out var rawCol)) return null;
        object? lineNumber = null;
        if (rawLine != null)
        {
            if (!TryReadInteger(rawLine, out var line) || line < 0 || line > MaxSafeInteger) return null;
            lineNumber = BoxInteger(line);
        }
        object? columnNumber = null;
        if (rawCol != null)
        {
            if (!TryReadInteger(rawCol, out var col) || col < 0 || col > MaxSafeInteger) return null;
            columnNumber = BoxInteger(col);
        }
        if (!raw.TryGetValue("message", out var rawMessage) || !TryScrubNullableString(rawMessage, additionalFields, exactFields, out var message)) return null;
        if (!raw.TryGetValue("file_name", out var rawFileName) || !TryScrubNullableString(rawFileName, additionalFields, exactFields, out var fileName)) return null;
        if (!raw.TryGetValue("target", out var rawTarget)) return null;

        Dictionary<string, object?>? targetOut = null;
        if (rawTarget != null)
        {
            if (rawTarget is not Dictionary<string, object?> targetMap || !HasOnlyAllowedKeys(targetMap, AllowedTargetKeys)) return null;
            if (!targetMap.TryGetValue("tag_name", out var rawTagName) || !targetMap.TryGetValue("source_url", out var rawSourceUrl)) return null;

            string? tagNameOut;
            if (rawTagName == null)
            {
                tagNameOut = null;
            }
            else if (rawTagName is string tagText)
            {
                if (AllowedHtmlResourceTags.Contains(AsciiLower(tagText)))
                {
                    tagNameOut = tagText;
                }
                else
                {
                    var sanitizedTag = Protect(new Dictionary<string, object?> { ["tag_name"] = tagText }, additionalFields, exactFields) as Dictionary<string, object?>;
                    if (sanitizedTag == null || !sanitizedTag.TryGetValue("tag_name", out var scrubbedTag) || scrubbedTag is not string scrubbedTagText)
                    {
                        return null;
                    }
                    tagNameOut = scrubbedTagText;
                }
            }
            else
            {
                return null;
            }

            if (!TryScrubNullableString(rawSourceUrl, additionalFields, exactFields, out var sourceUrl)) return null;
            targetOut = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tag_name"] = tagNameOut,
                ["source_url"] = sourceUrl
            };

            if (targetMap.TryGetValue("attributes", out var rawAttrs))
            {
                if (rawAttrs is not Dictionary<string, object?> attrsMap || !HasOnlyAllowedKeys(attrsMap, AllowedTargetAttrKeys)) return null;
                var attrsOut = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in AllowedTargetAttrStringKeys)
                {
                    if (!attrsMap.TryGetValue(key, out var attrValue)) continue;
                    if (attrValue is not string attrText) return null;
                    var scrubbedAttr = ScrubProtocolString(attrText, additionalFields, exactFields);
                    if (scrubbedAttr == null) return null;
                    attrsOut[key] = scrubbedAttr;
                }
                foreach (var key in AllowedTargetAttrBoolKeys)
                {
                    if (!attrsMap.TryGetValue(key, out var attrValue)) continue;
                    if (attrValue is not bool boolValue) return null;
                    attrsOut[key] = boolValue;
                }
                targetOut["attributes"] = attrsOut;
            }
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["message"] = message,
            ["file_name"] = fileName,
            ["line_number"] = lineNumber,
            ["column_number"] = columnNumber,
            ["target"] = targetOut,
            ["opaque"] = opaque
        };

        if (raw.TryGetValue("page", out var rawPage))
        {
            if (rawPage is not Dictionary<string, object?> pageMap || pageMap.Count != 4 || !HasOnlyAllowedKeys(pageMap, AllowedPageKeys)) return null;
            if (!pageMap.TryGetValue("url", out var rawPageUrl) || !TryScrubNullableString(rawPageUrl, additionalFields, exactFields, out var pageUrl)) return null;
            if (!pageMap.TryGetValue("referrer", out var rawReferrer) || !TryScrubNullableString(rawReferrer, additionalFields, exactFields, out var pageReferrer)) return null;
            if (!pageMap.TryGetValue("ready_state", out var rawReady) || !pageMap.TryGetValue("visibility_state", out var rawVisibility)) return null;
            if (rawReady != null && (rawReady is not string readyStr || !AllowedReadyStates.Contains(readyStr))) return null;
            if (rawVisibility != null && (rawVisibility is not string visStr || !AllowedVisibilityStates.Contains(visStr))) return null;

            output["page"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["url"] = pageUrl,
                ["referrer"] = pageReferrer,
                ["ready_state"] = rawReady,
                ["visibility_state"] = rawVisibility
            };
        }

        return output;
    }

    private static Dictionary<string, object?>? SanitizeRejectionReasonBlock(
        Dictionary<string, object?> raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        if (!HasOnlyAllowedKeys(raw, AllowedRejectionKeys)) return null;
        if (!raw.TryGetValue("kind", out var kindValue) || kindValue is not string kind || !AllowedRejectionKinds.Contains(kind)) return null;

        var output = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind };
        foreach (var field in new[] { "name", "message", "preview" })
        {
            if (!raw.TryGetValue(field, out var value)) continue;
            if (value is not string text || text.Length == 0) return null;
            var scrubbed = ScrubProtocolString(text, additionalFields, exactFields);
            if (scrubbed == null) return null;
            output[field] = scrubbed;
        }
        return output;
    }

    private static Dictionary<string, object?>? SanitizeExceptionEvidenceBlock(
        Dictionary<string, object?> raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        if (raw.Count != 6 || !HasOnlyAllowedKeys(raw, AllowedGraphKeys) || !IsVersionOne(raw)) return null;
        if (!raw.TryGetValue("runtime", out var runtimeValue) || runtimeValue is not string runtime || !AllowedExceptionRuntimes.Contains(runtime)) return null;
        if (!raw.TryGetValue("root_id", out var rootValue) || !TryReadInteger(rootValue, out var rootId) || rootId < 0 || rootId > 7) return null;
        if (!raw.TryGetValue("truncated", out var truncatedValue) || truncatedValue is not bool truncated) return null;
        if (!raw.TryGetValue("unavailable_reason", out var reasonValue)) return null;
        if (reasonValue != null && (reasonValue is not string reasonStr || !AllowedEvidenceLimitReasons.Contains(reasonStr))) return null;
        if (!raw.TryGetValue("nodes", out var nodesValue) || nodesValue is not List<object?> rawNodes || rawNodes.Count < 1 || rawNodes.Count > 8) return null;

        var adjacency = new Dictionary<int, List<int>>();
        var nodeEdges = new List<(int Id, List<Dictionary<string, object?>> Edges)>();
        var nodesOut = new List<object?>();

        foreach (var rawNodeObj in rawNodes)
        {
            if (rawNodeObj is not Dictionary<string, object?> node || node.Count != 8 || !HasOnlyAllowedKeys(node, AllowedNodeKeys)) return null;
            if (!node.TryGetValue("id", out var idValue) || !TryReadInteger(idValue, out var idLong) || idLong < 0 || idLong > 7) return null;
            var id = (int)idLong;
            if (adjacency.ContainsKey(id)) return null;
            adjacency[id] = new List<int>();

            if (!node.TryGetValue("name", out var rawName) || rawName is not string nameText || !FitsUtf8(nameText, 256)) return null;
            if (!node.TryGetValue("message", out var rawMessage) || rawMessage is not string messageText || !FitsUtf8(messageText, 2048)) return null;
            if (!node.TryGetValue("stack", out var rawStack)) return null;
            string? stackText = null;
            if (rawStack != null)
            {
                if (rawStack is not string s || !FitsUtf8(s, 16384)) return null;
                stackText = s;
            }
            if (!node.TryGetValue("trace_origin", out var originValue) || originValue is not string traceOrigin || !AllowedTraceOrigins.Contains(traceOrigin)) return null;
            if (!node.TryGetValue("parsing_status", out var statusValue) || statusValue is not string parsingStatus || !AllowedParsingStatuses.Contains(parsingStatus)) return null;
            if (!node.TryGetValue("frames", out var framesValue) || framesValue is not List<object?> rawFrames || rawFrames.Count > 32) return null;
            if (!node.TryGetValue("relationships", out var relsValue) || relsValue is not List<object?> rawRels || rawRels.Count > 5) return null;

            var name = ScrubProtocolString(nameText, additionalFields, exactFields);
            var message = ScrubProtocolString(messageText, additionalFields, exactFields);
            var stack = stackText == null ? null : ScrubProtocolString(SanitizeBrowserStackUrls(stackText), additionalFields, exactFields);
            if (name == null || message == null || (stackText != null && stack == null)) return null;

            var framesOut = new List<object?>();
            foreach (var rawFrameObj in rawFrames)
            {
                if (rawFrameObj is not Dictionary<string, object?> frame || frame.Count != 5 || !HasOnlyAllowedKeys(frame, AllowedFrameKeys)) return null;
                if (!frame.TryGetValue("function", out var rawFn) || !frame.TryGetValue("file", out var rawFile) ||
                    !frame.TryGetValue("line", out var rawLine) || !frame.TryGetValue("column", out var rawCol) ||
                    !frame.TryGetValue("classification", out var rawClass)) return null;

                string? fnOut = null;
                if (rawFn != null)
                {
                    if (rawFn is not string fnStr || !FitsUtf8(fnStr, 256)) return null;
                    fnOut = ScrubProtocolString(fnStr, additionalFields, exactFields);
                    if (fnOut == null) return null;
                }
                string? fileOut = null;
                if (rawFile != null)
                {
                    if (rawFile is not string fileStr || !FitsUtf8(fileStr, 2048)) return null;
                    fileOut = ScrubProtocolString(SanitizeBrowserStackUrls(fileStr), additionalFields, exactFields);
                    if (fileOut == null) return null;
                }
                object? lineOut = null;
                if (rawLine != null)
                {
                    if (!TryReadInteger(rawLine, out var line) || line <= 0 || line > MaxSafeInteger) return null;
                    lineOut = BoxInteger(line);
                }
                object? colOut = null;
                if (rawCol != null)
                {
                    if (!TryReadInteger(rawCol, out var col) || col < 0 || col > MaxSafeInteger) return null;
                    colOut = BoxInteger(col);
                }
                if (rawClass is not string classification || !AllowedFrameClassifications.Contains(classification)) return null;

                framesOut.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["function"] = fnOut,
                    ["file"] = fileOut,
                    ["line"] = lineOut,
                    ["column"] = colOut,
                    ["classification"] = classification
                });
            }

            var edgesOut = new List<object?>();
            var parsedEdges = new List<Dictionary<string, object?>>();
            foreach (var rawRelObj in rawRels)
            {
                if (rawRelObj is not Dictionary<string, object?> rel || rel.Count != 2 || !HasOnlyAllowedKeys(rel, AllowedRelationshipKeys)) return null;
                if (!rel.TryGetValue("kind", out var relKindVal) || relKindVal is not string relKind || !AllowedRelationshipKinds.Contains(relKind)) return null;
                if (!rel.TryGetValue("target_id", out var targetVal) || !TryReadInteger(targetVal, out var targetLong) || targetLong < 0 || targetLong > 7) return null;
                var edge = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = relKind,
                    ["target_id"] = (int)targetLong
                };
                edgesOut.Add(edge);
                parsedEdges.Add(edge);
            }
            nodeEdges.Add((id, parsedEdges));

            nodesOut.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = name,
                ["message"] = message,
                ["stack"] = stack,
                ["trace_origin"] = traceOrigin,
                ["parsing_status"] = parsingStatus,
                ["frames"] = framesOut,
                ["relationships"] = edgesOut
            });
        }

        foreach (var entry in nodeEdges)
        {
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);
            var causal = 0;
            foreach (var edge in entry.Edges)
            {
                var relKind = (string)edge["kind"]!;
                var targetId = (int)edge["target_id"]!;
                if (!adjacency.ContainsKey(targetId) || !seenEdges.Add(relKind + ":" + targetId)) return null;
                if (relKind is "cause" or "context") causal++;
                adjacency[entry.Id].Add(targetId);
            }
            if (causal > 1) return null;
        }

        var active = new HashSet<int>();
        var reached = new HashSet<int>();
        bool VisitDag(int id, int depth)
        {
            if (depth > 4 || !adjacency.TryGetValue(id, out var children) || !active.Add(id)) return false;
            reached.Add(id);
            foreach (var child in children)
            {
                if (!VisitDag(child, depth + 1)) return false;
            }
            active.Remove(id);
            return true;
        }
        if (!VisitDag((int)rootId, 0) || reached.Count != rawNodes.Count) return null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["runtime"] = runtime,
            ["root_id"] = (int)rootId,
            ["nodes"] = nodesOut,
            ["truncated"] = truncated,
            ["unavailable_reason"] = reasonValue
        };
    }

    private static Dictionary<string, object?>? SanitizeExecutionBlock(
        Dictionary<string, object?> raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        if (raw.Count != 5 || !HasOnlyAllowedKeys(raw, AllowedExecutionKeys) || !IsVersionOne(raw)) return null;
        if (!raw.TryGetValue("kind", out var kindValue) || kindValue is not string kind || !AllowedExecutionKinds.Contains(kind)) return null;
        if (!raw.TryGetValue("name", out var rawName) || !raw.TryGetValue("elapsed_ms", out var rawElapsed) || !raw.TryGetValue("limit_ms", out var rawLimit)) return null;

        string? nameOut = null;
        if (rawName != null)
        {
            if (rawName is not string nameText || !FitsUtf8(nameText, 128)) return null;
            nameOut = ScrubProtocolString(nameText, additionalFields, exactFields);
            if (nameOut == null) return null;
        }
        object? elapsedOut = null;
        if (rawElapsed != null && !TryReadNonNegativeNumber(rawElapsed, out elapsedOut)) return null;
        object? limitOut = null;
        if (rawLimit != null && !TryReadNonNegativeNumber(rawLimit, out limitOut)) return null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["kind"] = kind,
            ["name"] = nameOut,
            ["elapsed_ms"] = elapsedOut,
            ["limit_ms"] = limitOut
        };
    }

    private static Dictionary<string, object?>? SanitizeResourceObservationsBlock(
        Dictionary<string, object?> raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        if (raw.Count != 2 || !HasOnlyAllowedKeys(raw, AllowedResourceBlockKeys) || !IsVersionOne(raw)) return null;
        if (!raw.TryGetValue("items", out var itemsValue) || itemsValue is not List<object?> rawItems || rawItems.Count < 1 || rawItems.Count > 5) return null;

        var itemsOut = new List<object?>();
        foreach (var rawItemObj in rawItems)
        {
            if (rawItemObj is not Dictionary<string, object?> item || !item.TryGetValue("kind", out var kindValue) || kindValue is not string kind) return null;
            if (!item.TryGetValue("observed_at", out var obsValue) || obsValue is not string observedAt || !CanonicalEventSchema.ValidTimestamp(observedAt)) return null;
            if (!item.TryGetValue("source_url", out var rawSource)) return null;

            string? sourceUrl = null;
            if (rawSource != null)
            {
                if (rawSource is not string urlText || !EvidenceWire.ResourceUrl(urlText)) return null;
                sourceUrl = ScrubProtocolString(urlText, additionalFields, exactFields);
                if (sourceUrl == null) return null;
            }

            if (kind == "timing")
            {
                if (item.Count != 7 || !HasOnlyAllowedKeys(item, AllowedTimingItemKeys)) return null;
                if (!item.TryGetValue("availability", out var availVal) || availVal is not string availability || !AllowedResourceAvailabilities.Contains(availability)) return null;
                if (!item.TryGetValue("response_status", out var rawStatus) || !item.TryGetValue("duration_ms", out var rawDuration) || !item.TryGetValue("transfer_size_bytes", out var rawSize)) return null;

                object? statusOut = null;
                if (rawStatus != null)
                {
                    if (!TryReadInteger(rawStatus, out var status) || status < 100 || status > 599) return null;
                    statusOut = (int)status;
                }
                object? durationOut = null;
                if (rawDuration != null && !TryReadNonNegativeNumber(rawDuration, out durationOut)) return null;
                object? sizeOut = null;
                if (rawSize != null)
                {
                    if (!TryReadInteger(rawSize, out var size) || size < 0 || size > MaxSafeInteger) return null;
                    sizeOut = BoxInteger(size);
                }

                itemsOut.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "timing",
                    ["observed_at"] = observedAt,
                    ["source_url"] = sourceUrl,
                    ["availability"] = availability,
                    ["response_status"] = statusOut,
                    ["duration_ms"] = durationOut,
                    ["transfer_size_bytes"] = sizeOut
                });
            }
            else if (kind == "csp")
            {
                if (item.Count != 6 || !HasOnlyAllowedKeys(item, AllowedCspItemKeys)) return null;
                if (!item.TryGetValue("effective_directive", out var dirVal) || dirVal is not string directive || !CspDirectivePattern.IsMatch(directive)) return null;
                if (!item.TryGetValue("disposition", out var dispVal) || dispVal is not string disposition || !AllowedCspDispositions.Contains(disposition)) return null;
                if (!item.TryGetValue("document_status_code", out var rawDocStatus)) return null;

                object? docStatusOut = null;
                if (rawDocStatus != null)
                {
                    if (!TryReadInteger(rawDocStatus, out var docStatus) || docStatus < 100 || docStatus > 599) return null;
                    docStatusOut = (int)docStatus;
                }

                itemsOut.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = "csp",
                    ["observed_at"] = observedAt,
                    ["source_url"] = sourceUrl,
                    ["effective_directive"] = directive,
                    ["disposition"] = disposition,
                    ["document_status_code"] = docStatusOut
                });
            }
            else
            {
                return null;
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["items"] = itemsOut
        };
    }

    private static Dictionary<string, object?>? SanitizeDiagnosticContextPayload(
        Dictionary<string, object?> raw,
        IEnumerable<string>? additionalFields,
        IEnumerable<string>? exactFields)
    {
        if (!IsVersionOne(raw) || !raw.TryGetValue("kind", out var kindValue) || kindValue is not string kind) return null;
        if (!raw.TryGetValue("failure_ref", out var failureValue) || failureValue is not string failureRef || !EvidenceWire.FreshId(failureRef)) return null;
        if (!raw.TryGetValue("scope", out var rawScope)) return null;
        var scope = SanitizeScope(rawScope);
        if (scope == null) return null;

        if (kind == "recovery")
        {
            if (!HasOnlyAllowedKeys(raw, AllowedRecoveryKeys)) return null;
            if (!raw.TryGetValue("attempt_id", out var attemptValue) || attemptValue is not string attemptId || !EvidenceWire.FreshId(attemptId)) return null;
            if (!raw.TryGetValue("state", out var stateValue) || stateValue is not string state || !AllowedRecoveryStates.Contains(state)) return null;

            var output = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = 1,
                ["kind"] = "recovery",
                ["failure_ref"] = failureRef,
                ["scope"] = scope,
                ["attempt_id"] = attemptId,
                ["state"] = state
            };

            if (raw.TryGetValue("operation", out var rawOp))
            {
                if (rawOp is not string opText || !FitsUtf8(opText, 64)) return null;
                var scrubbedOp = ScrubProtocolString(opText, additionalFields, exactFields);
                if (scrubbedOp == null) return null;
                output["operation"] = scrubbedOp;
            }
            if (raw.TryGetValue("status_code", out var rawStatus))
            {
                if (!TryReadInteger(rawStatus, out var status) || status < 100 || status > 599) return null;
                output["status_code"] = (int)status;
            }
            if (raw.TryGetValue("duration_ms", out var rawDuration))
            {
                if (!TryReadNonNegativeNumber(rawDuration, out var duration)) return null;
                output["duration_ms"] = duration;
            }
            return output;
        }

        if (kind == "resource_observation")
        {
            if (raw.Count != 6 || !HasOnlyAllowedKeys(raw, AllowedResourceDiagnosticKeys)) return null;
            if (!raw.TryGetValue("source_event_id", out var sourceIdVal) || sourceIdVal is not string sourceEventId || !CanonicalEventSchema.IsUuid(sourceEventId)) return null;
            if (!raw.TryGetValue("observations", out var rawObs) || rawObs is not Dictionary<string, object?> obsMap) return null;
            var observations = SanitizeResourceObservationsBlock(obsMap, additionalFields, exactFields);
            if (observations == null) return null;

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = 1,
                ["kind"] = "resource_observation",
                ["failure_ref"] = failureRef,
                ["scope"] = scope,
                ["source_event_id"] = sourceEventId,
                ["observations"] = observations
            };
        }

        return null;
    }
}
