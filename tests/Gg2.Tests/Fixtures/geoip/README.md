# The geo-database fixture

`GeoIP2-City-Test.mmdb` is MaxMind's own test database, from
[maxmind/MaxMind-DB](https://github.com/maxmind/MaxMind-DB) `test-data/`.
Copyright (c) 2013-2026 MaxMind, Inc., dual licensed Apache-2.0 or MIT.

It is here because `GeoIpDatabaseTests` needs an **oracle** rather than a
mirror. A reader tested against an encoder written by the same author passes
whenever the two misread the specification the same way; this file was built by
the format's authors, and the addresses the tests look up are the ones their own
test suites assert on.

It is 22 KB, it exercises 24-bit and 28-bit records, pointers, nested maps and
subdivisions, and it is not a database of anywhere real — every address in it is
from a documentation or test range.

⛔ **Not a substitute for a real database at runtime.** Nothing ships one; the
operator supplies it. `docs/ANALYTICS.md` names the free sources.
