// Owner: integration. Pure helpers that reproduce, byte for byte, the Python behaviour the repository's F# tools were
// ported from (tools/progress from docs/quality/progress_from_tests.py, tools/metrics from docs/quality/code_metrics.py):
// json.dumps with an indent, float repr, round(x, n), str.splitlines, str.strip, text-mode reading, and the regular
// expression classes \s, \w and \b as Python's `re` defines them for str patterns. Shared by the tools' .fsx scripts
// (#load) and Causa.App.Tests.
module Causa.Tools.PythonCompat

open System
open System.Globalization
open System.Numerics
open System.Text

// ---- JSON --------------------------------------------------------------------------------------------------------

/// A JSON value as Python's json module writes it.
type Json =
    | JString of string
    | JInt of int64
    | JFloat of float
    | JArray of Json list
    | JObject of (string * Json) list

module Json =
    /// Python's json string escaping with ensure_ascii=True: the short escapes, then \uXXXX (lower-case hex) for every
    /// UTF-16 unit outside printable ASCII (space to '~').
    let quote (s: string) : string =
        let b = StringBuilder("\"")
        for c in s do
            match c with
            | '"' -> b.Append "\\\"" |> ignore
            | '\\' -> b.Append "\\\\" |> ignore
            | '\n' -> b.Append "\\n" |> ignore
            | '\r' -> b.Append "\\r" |> ignore
            | '\t' -> b.Append "\\t" |> ignore
            | '\b' -> b.Append "\\b" |> ignore
            | '\012' -> b.Append "\\f" |> ignore
            | c when c < ' ' || c > '~' -> b.Append("\\u").Append((int c).ToString("x4", CultureInfo.InvariantCulture)) |> ignore
            | c -> b.Append c |> ignore
        b.Append('"').ToString()

    /// Python's repr(float): the shortest round-tripping digits, in fixed notation when the decimal exponent is in
    /// (-4, 16], otherwise as d.ddde±XX. json.dumps writes NaN and the infinities as NaN, Infinity and -Infinity.
    let floatRepr (x: float) : string =
        if Double.IsNaN x then "NaN"
        elif Double.IsPositiveInfinity x then "Infinity"
        elif Double.IsNegativeInfinity x then "-Infinity"
        elif x = 0.0 then (if 1.0 / x < 0.0 then "-0.0" else "0.0")
        else
            // .NET's "R" is the shortest round-tripping representation; only its layout differs from Python's.
            let s = (abs x).ToString("R", CultureInfo.InvariantCulture)
            let mantissa, exponent =
                match s.IndexOf 'E' with
                | -1 -> s, 0
                | i -> s.Substring(0, i), Int32.Parse(s.Substring(i + 1), CultureInfo.InvariantCulture)
            let whole, fraction =
                match mantissa.IndexOf '.' with
                | -1 -> mantissa, ""
                | i -> mantissa.Substring(0, i), mantissa.Substring(i + 1)
            let all = whole + fraction
            let leading = all.Length - all.TrimStart('0').Length
            let digits = all.TrimStart('0').TrimEnd('0')
            // value = 0.<digits> x 10^point
            let point = whole.Length + exponent - leading
            let body =
                if point <= -4 || point > 16 then
                    let e = point - 1
                    let m = if digits.Length = 1 then digits else digits.Substring(0, 1) + "." + digits.Substring 1
                    m + "e" + (if e < 0 then "-" else "+") + (abs e).ToString("00", CultureInfo.InvariantCulture)
                elif point <= 0 then "0." + String('0', -point) + digits
                elif point >= digits.Length then digits + String('0', point - digits.Length) + ".0"
                else digits.Substring(0, point) + "." + digits.Substring point
            (if x < 0.0 then "-" else "") + body

    let rec private render (indent: int) (depth: int) (value: Json) : string =
        let pad n = String(' ', indent * n)
        let block opening closing (items: string list) =
            match items with
            | [] -> opening + closing
            | _ -> opening + "\n" + (items |> List.map (fun i -> pad (depth + 1) + i) |> String.concat ",\n") + "\n" + pad depth + closing
        match value with
        | JString s -> quote s
        | JInt i -> i.ToString(CultureInfo.InvariantCulture)
        | JFloat f -> floatRepr f
        | JArray items -> items |> List.map (render indent (depth + 1)) |> block "[" "]"
        | JObject fields -> fields |> List.map (fun (k, v) -> quote k + ": " + render indent (depth + 1) v) |> block "{" "}"

    /// Python's `json.dumps(value, indent=indent)` (indent >= 1; without the newline print adds).
    let dumps (indent: int) (value: Json) : string = render indent 0 value

// ---- Numbers -----------------------------------------------------------------------------------------------------

module Number =
    /// Python's round(x, digits) for digits >= 0: the exact binary value rounded to `digits` decimals, ties to even,
    /// returned as the nearest double.
    let round (digits: int) (x: float) : float =
        if Double.IsNaN x || Double.IsInfinity x || x = 0.0 then x
        else
            let bits = BitConverter.DoubleToInt64Bits x
            let negative = bits < 0L
            let biased = int ((bits >>> 52) &&& 0x7FFL)
            let fraction = bits &&& 0xFFFFFFFFFFFFFL
            // |x| = m * 2^e exactly
            let m, e = if biased = 0 then fraction, -1074 else (fraction ||| (1L <<< 52)), biased - 1075
            if e >= 0 then x
            else
                let scale = BigInteger.Pow(BigInteger 10, digits)
                let numerator = BigInteger m * scale
                let denominator = BigInteger.Pow(BigInteger 2, -e)
                let q = BigInteger.Divide(numerator, denominator)
                let twice = (numerator - q * denominator) * BigInteger 2
                let rounded =
                    match compare twice denominator with
                    | c when c > 0 -> q + BigInteger.One
                    | 0 when not q.IsEven -> q + BigInteger.One
                    | _ -> q
                // The quotient of two exactly representable values is correctly rounded: the double nearest to
                // rounded / 10^digits, as Python's string-to-float step gives.
                let r = Double.Parse((rounded.ToString() + "E-" + string digits), CultureInfo.InvariantCulture)
                if negative then -r else r

// ---- Text --------------------------------------------------------------------------------------------------------

module Text =
    /// Python's str.isspace for one UTF-16 unit (adds the information separators U+001C..U+001F to .NET's set).
    let isSpace (c: char) = Char.IsWhiteSpace c || (c >= '\u001c' && c <= '\u001f')

    /// Python's str.strip() with no arguments.
    let strip (s: string) : string =
        let rec first i = if i < s.Length && isSpace s[i] then first (i + 1) else i
        let rec last i = if i > 0 && isSpace s[i - 1] then last (i - 1) else i
        let a = first 0
        let b = max a (last s.Length)
        s.Substring(a, b - a)

    let private isLineBoundary (c: char) =
        match c with
        | '\n' | '\r' | '\u000b' | '\u000c' | '\u001c' | '\u001d' | '\u001e' | '\u0085' | ' ' | ' ' -> true
        | _ -> false

    /// Python's str.splitlines(): every line boundary Python recognises, \r\n as one, no empty last line.
    let splitlines (s: string) : string list =
        let rec go (start: int) (i: int) (acc: string list) =
            if i >= s.Length then List.rev (if start < s.Length then s.Substring start :: acc else acc)
            elif isLineBoundary s[i] then
                let next = if s[i] = '\r' && i + 1 < s.Length && s[i + 1] = '\n' then i + 2 else i + 1
                go next next (s.Substring(start, i - start) :: acc)
            else go start (i + 1) acc
        go 0 0 []

    /// The text Python's Path.read_text(errors='ignore') gives for these bytes: UTF-8 with undecodable bytes dropped
    /// (a byte-order mark is kept as U+FEFF), then universal newlines (\r\n and \r become \n).
    let decode (bytes: byte array) : string =
        let utf8 = Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, DecoderReplacementFallback "")
        utf8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n')

    /// Non-overlapping occurrences, as Python's str.count.
    let count (needle: string) (haystack: string) : int =
        let rec go (from: int) (n: int) =
            match haystack.IndexOf(needle, from, StringComparison.Ordinal) with
            | -1 -> n
            | i -> go (i + needle.Length) (n + 1)
        if needle = "" then haystack.Length + 1 else go 0 0

// ---- Regular expressions -----------------------------------------------------------------------------------------

/// Python `re` classes for str patterns, written as .NET regex fragments. Python's \w is "alphanumeric or underscore"
/// (letters and every numeric category, but not combining marks or other connector punctuation, which .NET's \w
/// includes); its \s is str.isspace; its \b is the edge between those word and non-word characters.
module Re =
    let s = @"[\s\x1c-\x1f]"
    let w = @"[\p{L}\p{Nd}\p{Nl}\p{No}_]"
    let b = sprintf @"(?:(?<=%s)(?!%s)|(?<!%s)(?=%s))" w w w w
