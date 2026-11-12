// A browser spec.
/* a block
   comment */ const x = 1;
test("x", () => {
  /* inline */ expect(x).toBe(1); // trailing
});
