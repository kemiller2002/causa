module Unit.Tests

// Tests count as test code.
let ``adds`` () =
    if 1 + 1 <> 2 then failwith "arithmetic"

(* unterminated block comment
let ``ignored`` () = ()
