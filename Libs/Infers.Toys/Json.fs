// Copyright (C) by Vesa Karvonen

namespace Infers.Toys

open System
open System.IO
open System.Text
open System.Collections.Generic
open Infers
open Infers.Rep
open FParsec.Primitives
open FParsec.CharParsers
open PPrint

module Json =
  module Choice =
    let inline fold f g = function Choice1Of2 x -> f x
                                 | Choice2Of2 y -> g y
    let inline map f g = fold (f >> Choice1Of2) (g >> Choice2Of2)

  type Obj = Map<string, Value>
  and Value =
   | Obj of Obj
   | List of list<Value>
   | String of string
   | Number of string
   | Bool of bool
   | Nil

  let t = Bool true
  let f = Bool false

  let inline toMap kvs =
    List.fold
     (fun m (k, v) ->
        if Map.containsKey k m
        then failwithf "duplicate key: \"%s\"" k
        else Map.add k v m)
     Map.empty
     kvs

  type N = NumberLiteralOptions

  let tString =
    skipChar '\"' >>= fun () ->
    let b = StringBuilder ()
    let (outside, outside') = createParserForwardedToRef ()
    let (escaped, escaped') = createParserForwardedToRef ()
    let app (c: char) = b.Append c |> ignore ; outside
    outside' :=
      anyChar >>= function
       | '\"' -> spaces >>% b.ToString ()
       | '\\' -> escaped
       | c -> app c
    escaped' :=
      anyChar >>= function
       | '\"' -> app '\"'
       | '\\' -> app '\\'
       | '/'  -> app '/'
       | 'b'  -> app '\b'
       | 'f'  -> app '\f'
       | 'n'  -> app '\n'
       | 'r'  -> app '\r'
       | 't'  -> app '\t'
       | 'u'  ->
         (pipe4 hex hex hex hex <| fun d1 d2 d3 d4 ->
          let inline d d s =
            s * 16 + int d - (if   '0' <= d && d <= '9' then int '0'
                              elif 'a' <= d && d <= 'f' then int 'a' - 10
                              else                           int 'A' - 10)
          0 |> d d1 |> d d2 |> d d3 |> d d4 |> char) >>= app
       | c -> sprintf "Invalid escape: %A" c |> fail
    outside
  let tNumber =
    numberLiteral (N.AllowMinusSign ||| N.AllowFraction ||| N.AllowExponent)
     "number" |>> (fun n -> n.String) .>> spaces
  let tNull =
    skipString "null" .>> spaces
  let tBool =
    choiceL [skipString "true" >>% true
             skipString "false" >>% false] "boolean"
  let (pJSON', pJSON'') = createParserForwardedToRef ()
  let inline p c = skipChar c .>> spaces
  let pObject = p '{' >>. sepBy (tString .>> p ':' .>>. pJSON') (p ',') .>> p '}'
  let pArray = p '[' >>. sepBy pJSON' (p ',') .>> p ']'
  do pJSON'' :=
     choiceL [pObject |>> (toMap >> Obj)
              pArray  |>> List
              tString |>> String
              tNumber |>> Number
              tBool   |>> Bool
              tNull    >>%Nil] "value"
  let pJSON = spaces >>. pJSON'

  let tryOfString string =
    runParserOnString pJSON () "string" string
    |> function Success (result, (), _) -> Choice1Of2 result
              | Failure (error, _, ()) -> Choice2Of2 error
  let ofString string =
    tryOfString string |> Choice.fold id (failwithf "Expected: %s")

  //////////////////////////////////////////////////////////////////////////////

  let pNil = txt "null"
  let pTrue = txt "true"
  let pFalse = txt "false"
  let colonLn = colon <^> line
  let commaLn = comma <^> line

  let inline isPrintable c = 32 <= int c && int c <= 255

  let pString s =
    let b = StringBuilder ()
    let inline appc (c: char) = b.Append c |> ignore
    let inline apps (s: string) = b.Append s |> ignore
    appc '"'
    String.iter
     <| function
         | '\"' -> apps "\\\""
         | '\\' -> apps "\\\\"
         | '\b' -> apps "\\b"
         | '\f' -> apps "\\f"
         | '\n' -> apps "\\n"
         | '\r' -> apps "\\r"
         | '\t' -> apps "\\t"
         | c ->
           if isPrintable c then
             appc c
           else
             let inline d x =
               let c = (x >>> 12) &&& 0xF
               (if c < 10 then int '0' else int 'a' - 10) + c |> char |> appc
               x <<< 4
             apps "\\u"
             int c |> d |> d |> d |> d |> ignore
     <| s
    appc '"'
    b.ToString () |> txt

  let rec toDoc = function
    | Nil        -> pNil
    | Bool true  -> pTrue
    | Bool false -> pFalse
    | Number s   -> txt s
    | String s   -> pString s
    | List vs    ->
      vs |> Seq.map toDoc |> punctuate commaLn |> hcat |> brackets |> gnest 1
    | Obj kvs ->
      kvs
      |> Seq.map ^ fun kv ->
           gnest 1 (pString kv.Key <^> colonLn <^> toDoc kv.Value)
      |> punctuate commaLn |> hcat |> braces |> gnest 1

  let toString v = toDoc v |> PPrint.render None

  //////////////////////////////////////////////////////////////////////////////

  type Is<'t> = | Is

  type JsonValue<'t> =
    {OfJson: Value -> Choice<'t, string>
     ToJson: 't -> Value}

  type [<AbstractClass>] JsonObj<'t> () =
    abstract OfJson: Obj * byref<'t> -> option<string>
    abstract ToJson: byref<'t> * Obj -> Obj

  type [<AbstractClass>] JsonArray<'t> () =
    abstract OfJson: list<Value> * byref<'t> -> option<string>
    abstract ToJson: byref<'t> -> list<Value>

  type JsonA<'e,'r,   't> = A of JsonArray<'e>
  type JsonP<'e,'r,'o,'t> = P of JsonObj<'e>
  type JsonS<'p,'o,   't> = S of list<JsonValue<'t>>

  let number (ofString: string -> 't) (toString: 't -> string) =
    {OfJson = function (Number v) -> v |> ofString |> Choice1Of2
                     | _ -> Choice2Of2 "number"
     ToJson = toString >> Number}

  type [<Rep>] Json () =
    inherit Rules ()

    static member Null =
      {OfJson = function Nil -> Choice1Of2 () | _ -> Choice2Of2 "null"
       ToJson = function () -> Nil}
    static member Bool =
      {OfJson = function Bool v -> Choice1Of2 v | _ -> Choice2Of2 "bool"
       ToJson = Bool}
    static member Int32 = number int string
    static member Float = number float ^ sprintf "%.17g"
    static member Int64 = number int64 string
    static member String =
      {OfJson = function String v -> Choice1Of2 v | _ -> Choice2Of2 "string"
       ToJson = String}

    static member Literal () : JsonValue<Is<'t>> =
      let name = typeof<'t>.Name
      let nameJ = String name
      {OfJson = function String v when v = name -> Choice1Of2 Is
                       | _ -> Choice2Of2 name
       ToJson = fun Is -> nameJ}

    static member Array (tJ: JsonValue<'t>) =
      {OfJson = function
        | List js ->
          let n = List.length js
          let ts = Array.zeroCreate n
          let rec lp i js =
            match js with
             | [] -> Choice1Of2 ts
             | j::js ->
               match tJ.OfJson j with
                | Choice1Of2 v ->
                  ts.[i] <- v
                  lp (i+1) js
                | Choice2Of2 expected ->
                  Choice2Of2 ^ "[" + expected + "]"
          lp 0 js
        | _ -> Choice2Of2 "[ ... ]"
       ToJson = Array.map tJ.ToJson >> List.ofArray >> List}

    static member List (tsJ: JsonValue<array<'t>>) =
      {OfJson = tsJ.OfJson >> Choice.map List.ofArray id
       ToJson = Array.ofList >> tsJ.ToJson}

    static member Dictionary (tJ: JsonValue<'t>) =
      {OfJson = function
        | Obj o ->
          let d = Dictionary<_, _> ()
          use e = (o :> seq<_>).GetEnumerator ()
          let rec lp () =
            if e.MoveNext () then
              let kv = e.Current
              match tJ.OfJson kv.Value with
               | Choice1Of2 v ->
                 d.Add (kv.Key, v)
                 lp ()
               | Choice2Of2 expected ->
                 Choice2Of2 ^ "{\"...\":" + expected + "}"
            else
              Choice1Of2 d
          lp ()
        | _ ->
          Choice2Of2 "{ ... }"
       ToJson = Seq.map (fun kv -> (kv.Key, tJ.ToJson kv.Value)) >> Map >> Obj}

    static member Map (dtJ: JsonValue<Dictionary<string, 't>>) =
      {OfJson = dtJ.OfJson >> Choice.map Map.ofDictionary id
       ToJson = Dictionary<_, _> >> dtJ.ToJson}

    static member Item (_: Item<'e,'r,'t>, eJ: JsonValue<'e>) =
      let name = Some typeof<'e>.Name
      A {new JsonArray<'e> () with
          member jm.OfJson (vs, e) =
            match vs with
             | v::_ ->
               match eJ.OfJson v with
                | Choice1Of2 v -> e <- v ; None
                | Choice2Of2 e -> Some e
             | [] ->
               name
          member jm.ToJson (e) =
            [eJ.ToJson e]} : JsonA<'e,'r,'t>

    static member Pair (A eJ: JsonA<     'e,    Pair<'e,'r>,'t>,
                        A rJ: JsonA<        'r,         'r ,'t>)
                            : JsonA<Pair<'e,'r>,Pair<'e,'r>,'t>  =
      let nameComma = typeof<'e>.Name + ", "
      let nameCommaEllipsis = Some ^ nameComma + "..."
      A {new JsonArray<Pair<'e,'r>> () with
          member jm.OfJson (vs, er) =
           match eJ.OfJson (vs, &er.Elem) with
            | None ->
              match vs with
               | _::vsRest ->
                 match rJ.OfJson (vsRest, &er.Rest) with
                  | None ->
                    eJ.OfJson (vs, &er.Elem)
                  | Some e ->
                    Some ^ nameComma + e
               | _ ->
                 nameCommaEllipsis
            | some ->
              some
          member jm.ToJson (er) =
            eJ.ToJson (&er.Elem) @ rJ.ToJson (&er.Rest)}

    static member Product (asP: AsPairs<'p,'o,'t>, A pJ: JsonA<'p,'p,'t>) =
      {OfJson = function
        | List o ->
          let mutable p = Unchecked.defaultof<_>
          match pJ.OfJson (o, &p) with
           | None -> asP.Create (&p) |> Choice1Of2
           | Some e -> Choice2Of2 ^ "[" + e + "]"
        | _ -> Choice2Of2 "[ ... ]"
       ToJson = fun t ->
         let mutable p = asP.ToPairs t
         pJ.ToJson (&p) |> List}

    static member Elem (eL: Labelled<'e,'r,'o,'t>, eJ: JsonValue<'e>) =
      P {new JsonObj<'e> () with
          member jm.OfJson (o, e) =
            match Map.tryFind eL.Name o with
             | None -> Some ^ eL.Name + ": ..."
             | Some v ->
               match eJ.OfJson v with
                | Choice1Of2 v -> e <- v ; None
                | Choice2Of2 e -> Some ^ eL.Name + ":" + e
          member jm.ToJson (e, o) =
            Map.add eL.Name (eJ.ToJson e) o} : JsonP<'e,'r,'o,'t>

    static member OptElem (eL: Labelled<option<'e>,'r,'o,'t>, eJ: JsonValue<'e>)
                             :    JsonP<option<'e>,'r,'o,'t> =
      P {new JsonObj<option<'e>> () with
          member jm.OfJson (o, eO) =
           match Map.tryFind eL.Name o with
            | None -> eO <- None; None
            | Some v ->
              match eJ.OfJson v with
               | Choice1Of2 v -> eO <- Some v; None
               | Choice2Of2 e -> Some ^ eL.Name + ":" + e
          member jm.ToJson (eO, o) =
            match eO with
             | None -> o
             | Some e -> Map.add eL.Name (eJ.ToJson e) o}

    static member Pair (P eJ: JsonP<     'e,    Pair<'e,'r>,'o,'t>,
                        P rJ: JsonP<        'r,         'r ,'o,'t>)
                            : JsonP<Pair<'e,'r>,Pair<'e,'r>,'o,'t> =
      P {new JsonObj<Pair<'e,'r>> () with
          member jm.OfJson (o, er) =
            match eJ.OfJson (o, &er.Elem) with
             | None -> rJ.OfJson (o, &er.Rest)
             | some -> some
          member jm.ToJson (er, o) =
            rJ.ToJson (&er.Rest, eJ.ToJson (&er.Elem, o))}

    static member Product (asP: AsPairs<'p,'o,'t>, P pJ: JsonP<'p,'p,'o,'t>) =
      {OfJson = function
        | Obj o ->
          let mutable p = Unchecked.defaultof<_>
          match pJ.OfJson (o, &p) with
           | None -> asP.Create (&p) |> Choice1Of2
           | Some e -> Choice2Of2 ^ "{" + e + "}"
        | _ -> Choice2Of2 "{ ... }"
       ToJson = fun t ->
         let mutable p = asP.ToPairs t
         pJ.ToJson (&p, Map.empty) |> Obj}

    static member Case (_: Case<Empty,'o,'t>) : JsonS<Empty,'o,'t> =
      failwithf "Empty cases not supported, see type: %s" typeof<'t>.Name

    static member Case (m: Case<Pair<'e,'r>,'o,'t>,
                        pJ: JsonP<Pair<'e,'r>,Pair<'e,'r>,'o,'t>) =
      S [Json.Product (m, pJ)] : JsonS<Pair<'e,'r>,'o,'t>

    static member Case (m: Case<'e,'o,'t>,
                        eL: Labelled<'e,'e,'o,'t>,
                        eJ: JsonValue<'e>) =
      S [(if eL.Name = "Item"
          then {OfJson = eJ.OfJson >> Choice.map m.OfPairs id
                ToJson = m.ToPairs >> eJ.ToJson}
          else Json.Product (m, Json.Elem (eL, eJ)))] : JsonS<'e,'o,'t>

    static member Choice (S pJ: JsonS<       'p    , Choice<'p,'o>,'t>,
                          S oJ: JsonS<          'o ,           'o ,'t>) =
      S (pJ @ oJ)             : JsonS<Choice<'p,'o>, Choice<'p,'o>,'t>

    static member Sum (asC: AsChoices<'p,'t>, S pJ: JsonS<'p,'p,'t>) =
      let pJ = Array.ofList pJ
      {OfJson = fun v ->
        let rec lp es i =
          if i < pJ.Length
          then match pJ.[i].OfJson v with
                | Choice2Of2 e -> lp (e::es) (i+1)
                | success -> success
          else String.concat "|" es |> Choice2Of2
        lp [] 0
       ToJson = fun t -> pJ.[asC.Tag t].ToJson t}

    static member Value =
      {OfJson = Choice1Of2
       ToJson = id}

  let toJson<'t> t = generateDFS<Json, JsonValue<'t>>.ToJson t
  let tryOfJson<'t> j = generateDFS<Json, JsonValue<'t>>.OfJson j
  let ofJson<'t> j =
    tryOfJson<'t> j |> Choice.fold id  (failwithf "Expected: %s")

  let toJsonString<'t> t = toJson<'t> t |> toString
  let ofJsonString<'t> s = ofString s |> ofJson<'t>
  let tryOfJsonString<'t> s =
    tryOfString s |> Choice.fold tryOfJson<'t> Choice2Of2