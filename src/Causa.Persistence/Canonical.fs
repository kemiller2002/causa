// Owner: persistence. causa-storage wire conventions (CONTRACT v1.0.0 section 2): strict JSON, RFC 8785 canonical
// form, digests and event identity.
namespace Causa.Persistence

open System
open System.Security.Cryptography
open System.Text

/// Why a text is not a JSON document the contract accepts (stage 3, CAUSA.JSON_MALFORMED).
type JsonRefusal = JsonRefusal of string

[<RequireQualifiedAccess>]
module Canonical =

    let maxSafeInteger = 9007199254740991M

    let private utf8Strict = UTF8Encoding(false, true)

    /// Decodes UTF-8, refusing invalid sequences (stage 2).
    let decodeUtf8 (bytes: byte[]) : string option =
        try
            Some(utf8Strict.GetString bytes)
        with :? DecoderFallbackException ->
            None

    let utf8 (s: string) : byte[] = Encoding.UTF8.GetBytes s

    let sha256Hex (bytes: byte[]) = SHA256.HashData(bytes) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let private integerText = RegularExpressions.Regex(@"^-?(0|[1-9][0-9]*)\z", RegularExpressions.RegexOptions.Compiled)

    let rec private hasLoneSurrogate (s: string) =
        let mutable bad = false
        let mutable i = 0
        while not bad && i < s.Length do
            let c = s[i]
            if Char.IsHighSurrogate c then
                if i + 1 < s.Length && Char.IsLowSurrogate s[i + 1] then i <- i + 2
                else bad <- true
            elif Char.IsLowSurrogate c then bad <- true
            else i <- i + 1
        bad

    /// Raw-text scan for an escaped lone surrogate or an escaped U+FEFF, which a decoder would silently replace.
    let private escapedRefusal (text: string) : string option =
        let m = RegularExpressions.Regex.Matches(text, @"\\u([0-9a-fA-F]{4})")
        let codes = [ for x in m -> Convert.ToInt32(x.Groups[1].Value, 16), x.Index ]
        let rec scan (items: (int * int) list) =
            match items with
            | [] -> None
            | (0xFEFF, _) :: _ -> Some "U+FEFF is forbidden"
            | (h, i) :: (l, j) :: rest when h >= 0xD800 && h <= 0xDBFF && l >= 0xDC00 && l <= 0xDFFF && j = i + 6 -> scan rest
            | (c, _) :: _ when c >= 0xD800 && c <= 0xDFFF -> Some "unpaired UTF-16 surrogate"
            | _ :: rest -> scan rest
        // An escaped backslash before "u" is not an escape; such texts are rare in data and are judged by the decoder.
        if text.Contains "\\\\u" then None else scan codes

    let rec private ofElement (depth: int) (e: System.Text.Json.JsonElement) : Result<Json, string> =
        match e.ValueKind with
        | System.Text.Json.JsonValueKind.Object ->
            if depth > 128 then Error "nesting deeper than 128"
            else
                let props = [ for p in e.EnumerateObject() -> p.Name, p.Value ]
                let names = props |> List.map fst
                if List.length (List.distinct names) <> List.length names then Error "duplicate object key"
                elif names |> List.exists (fun n -> n.Contains '﻿' || hasLoneSurrogate n) then Error "invalid key text"
                else
                    let rec go acc rest =
                        match rest with
                        | [] -> Ok(Json.Object(List.rev acc))
                        | (k, v) :: tail ->
                            match ofElement (depth + 1) v with
                            | Ok j -> go ((k, j) :: acc) tail
                            | Error e -> Error e
                    go [] props
        | System.Text.Json.JsonValueKind.Array ->
            if depth > 128 then Error "nesting deeper than 128"
            else
                let rec go acc (rest: System.Text.Json.JsonElement list) =
                    match rest with
                    | [] -> Ok(Json.Array(List.rev acc))
                    | v :: tail ->
                        match ofElement (depth + 1) v with
                        | Ok j -> go (j :: acc) tail
                        | Error e -> Error e
                go [] (List.ofSeq (e.EnumerateArray()))
        | System.Text.Json.JsonValueKind.String ->
            match e.GetString() with
            | null -> Error "invalid string"
            | s when s.Contains '﻿' -> Error "U+FEFF is forbidden"
            | s when hasLoneSurrogate s -> Error "unpaired UTF-16 surrogate"
            | s -> Ok(Json.String s)
        | System.Text.Json.JsonValueKind.Number ->
            let raw = e.GetRawText()
            if not (integerText.IsMatch raw) then Error(sprintf "number %s is not an integer" raw)
            else
                match Decimal.TryParse(raw, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
                | true, d when abs d <= maxSafeInteger -> Ok(Json.Number d)
                | _ -> Error(sprintf "integer %s is out of range" raw)
        | System.Text.Json.JsonValueKind.True -> Ok(Json.Bool true)
        | System.Text.Json.JsonValueKind.False -> Ok(Json.Bool false)
        | _ -> Ok Json.Null

    /// One JSON text by the contract's rules (stage 3): duplicate keys, non-integers, out-of-range integers, nesting
    /// deeper than 128, unpaired surrogates and U+FEFF are refused.
    let parse (text: string) : Result<Json, JsonRefusal> =
        match escapedRefusal text with
        | Some why -> Error(JsonRefusal why)
        | None ->
            try
                let options = System.Text.Json.JsonDocumentOptions(MaxDepth = 256, AllowTrailingCommas = false, CommentHandling = System.Text.Json.JsonCommentHandling.Disallow)
                use doc = System.Text.Json.JsonDocument.Parse(text, options)
                ofElement 1 doc.RootElement |> Result.mapError JsonRefusal
            with
            | :? System.Text.Json.JsonException as e -> Error(JsonRefusal e.Message)
            | :? ArgumentException as e -> Error(JsonRefusal e.Message)
            | :? InvalidOperationException as e -> Error(JsonRefusal e.Message)

    let private escape (sb: StringBuilder) (s: string) =
        sb.Append '"' |> ignore
        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\f' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore
        sb.Append '"' |> ignore

    let rec private write (sb: StringBuilder) (j: Json) =
        match j with
        | Json.Null -> sb.Append "null" |> ignore
        | Json.Bool true -> sb.Append "true" |> ignore
        | Json.Bool false -> sb.Append "false" |> ignore
        | Json.Number n -> sb.Append(Decimal.Truncate(n).ToString(Globalization.CultureInfo.InvariantCulture)) |> ignore
        | Json.String s -> escape sb s
        | Json.Array items ->
            sb.Append '[' |> ignore
            items |> List.iteri (fun i v -> (if i > 0 then sb.Append ',' |> ignore); write sb v)
            sb.Append ']' |> ignore
        | Json.Object props ->
            sb.Append '{' |> ignore
            props
            |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            |> List.iteri (fun i (k, v) ->
                (if i > 0 then sb.Append ',' |> ignore)
                escape sb k
                sb.Append ':' |> ignore
                write sb v)
            sb.Append '}' |> ignore

    /// RFC 8785 (JCS) canonical text: keys sorted by UTF-16 code units, no whitespace, JCS string escaping.
    let serialize (j: Json) : string =
        let sb = StringBuilder()
        write sb j
        sb.ToString()

    /// A JSON file: one canonical text and exactly one LF.
    let fileBytes (j: Json) : byte[] = utf8 (serialize j + "\n")

    /// SHA-256 of a JSON document's canonical text without the trailing LF (section 2.11).
    let digest (j: Json) = sha256Hex (utf8 (serialize j))

    /// The Git blob id of some bytes (SHA-1 of "blob <length>\0" + bytes): equal ids mean equal bytes.
    let gitBlobSha (bytes: byte[]) =
        let header = Encoding.ASCII.GetBytes(sprintf "blob %d\000" bytes.Length)
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    /// UUIDv5 (RFC 4122 section 4.3, SHA-1).
    let uuidV5 (ns: Guid) (name: string) : Guid =
        let nsBytes = ns.ToByteArray(true)
        let hash = SHA1.HashData(Array.append nsBytes (utf8 name))
        let b = Array.sub hash 0 16
        b[6] <- (b[6] &&& 0x0Fuy) ||| 0x50uy
        b[8] <- (b[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(ReadOnlySpan<byte>(b), true)

    /// N = UUIDv5(NAMESPACE_URL, ".../contract/v1/event-id") (section 5.2).
    let eventIdNamespace = Guid "241fefd4-c810-5a3a-9cfa-4bbd968ae1cb"

    let guidText (g: Guid) = g.ToString("D").ToLowerInvariant()

    /// eventId = UUIDv5(N, investigationId + "/" + aggregateVersion + "/" + correlationId) (section 5.2).
    let eventId (investigationId: string) (version: int64) (correlationId: string) : string =
        guidText (uuidV5 eventIdNamespace (sprintf "%s/%d/%s" investigationId version correlationId))

    /// RFC 3339 UTC with exactly three fractional digits (section 2.9). None for a sub-millisecond instant, which a
    /// writer refuses rather than truncates (CAUSA.UNREPRESENTABLE).
    let timestamp (t: DateTimeOffset) : string option =
        let u = t.ToUniversalTime()
        if u.Ticks % TimeSpan.TicksPerMillisecond <> 0L then None
        else Some(u.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Globalization.CultureInfo.InvariantCulture))

    let private timestampPattern = RegularExpressions.Regex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z\z", RegularExpressions.RegexOptions.Compiled)

    /// A timestamp in the contract's form that is a real instant (stage 7).
    let parseTimestamp (s: string) : DateTimeOffset option =
        if not (timestampPattern.IsMatch s) then None
        else
            match DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal) with
            | true, t -> Some t
            | _ -> None

    let private uuidPattern = RegularExpressions.Regex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegularExpressions.RegexOptions.Compiled)

    let isUuid (s: string) = uuidPattern.IsMatch s && s <> "00000000-0000-0000-0000-000000000000"

    /// Non-blank by the engine's whitespace set (section 2.6), judged per UTF-16 code unit.
    let isNonBlank (s: string) =
        let blank (c: char) =
            (c >= '\u0009' && c <= '\u000D') || c = ' ' || c = '\u0085' || c = ' ' || c = ' ' || (c >= ' ' && c <= ' ')
            || c = ' ' || c = ' ' || c = ' ' || c = ' ' || c = '　'
        s |> Seq.exists (blank >> not)
