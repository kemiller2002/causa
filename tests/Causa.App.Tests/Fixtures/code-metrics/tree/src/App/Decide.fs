module App.Decide

(* a block comment
   let hidden x = if x then 1 else 2 *)
// a line comment

type Choice = A | B

let decide (x: int) (flag: bool) =
    match x with
    | 0 when flag -> "zero"
    | 1 -> "one"
    | n when n > 10 && flag || n < -10 -> "far"
    | _ ->
        if flag then "flag"
        elif x > 5 then "big"
        else "other"

let private loop (xs: int list) =
    let mutable total = 0
    for x in xs do
        total <- total + x
    while total > 100 do
        total <- total - 100
    total

let rec walk (n: int) =
    try
        if n <= 0 then Ok n
        else walk (n - 1)
    with ex ->
        Error ex.Message

[<AutoOpen>]
module Inner =
    let shortOne = 1
    let helper (o: int option) =
        while false do ()
        for _ in [] do ()
        if o.IsSome && o.Value > 0 || o.Value < -5 then
            o
            |> Option.map ((+) 1)
            |> Option.defaultValue 0
        elif o.IsNone then 0
        else -1
