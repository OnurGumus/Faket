module Paket.Json

open System.Text.Json
open System.Text.Json.Serialization

/// Shared System.Text.Json options used across Paket, chosen to mirror the leniency
/// of the Newtonsoft.Json defaults this code was written against:
///  - case-insensitive property matching
///  - tolerant of trailing commas and // comments
let options =
    let o = JsonSerializerOptions()
    o.PropertyNameCaseInsensitive <- true
    o.AllowTrailingCommas <- true
    o.ReadCommentHandling <- JsonCommentHandling.Skip
    o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    o

let deserialize<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, options)

let serialize (value: 'T) : string =
    JsonSerializer.Serialize<'T>(value, options)
