namespace App

type Shape =
    | Circle of float
    | Square of float
    member this.Area =
        match this with
        | Circle r -> 3.14 * r * r
        | Square s -> s * s
    override this.ToString() =
        match this with
        | Circle _ -> "circle"
        | Square _ -> "square"
        || false |> ignore

and Box = { Shape: Shape }
    with
    member this.Name = this.Shape.ToString()

let inline größe (x: float) =
    x * x
    |> id
    |> id

let mutable counter = 0
counter <- counter + 1
