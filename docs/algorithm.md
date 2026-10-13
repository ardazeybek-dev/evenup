# How evenup settles a group

The pipeline is **parse → split → convert → balance → settle**. Each step is a pure function in `src/Evenup.Core`.

## 1. Money never touches floating point

Every amount is an `int64` count of the currency's **minor unit**: cents for EUR, whole yen for JPY, fils (1/1000) for KWD. The parser turns `"126.40"` into `12640` using the invariant culture, and it rejects precision the currency doesn't have. As a result, `0.1 + 0.2` problems cannot happen, and a ledger reads the same on a machine set to Turkish, German or English.

## 2. Splitting without losing a cent

Dividing €100.00 three ways gives 3333.33… cents per person. evenup uses the **largest-remainder (Hamilton) method**:

1. Give everyone the floor of their exact share: `total × weight / Σweights`. This is computed with `BigInteger`, so huge weights can't overflow.
2. Hand out the cents that are left over, one each, to the people with the largest fractional remainders. Ties go to whoever was listed first.

This gives two guarantees:

- The parts always add up to exactly the total.
- Every part is within one minor unit of its ideal value.

Property tests check both on random inputs. The output is deterministic, so a report generated today matches one generated next year.

Itemized receipts apply the method twice. Each shared item is split equally between its people. Then the uncovered rest (tax, tip, service or discount) is split in proportion to each person's subtotal.

## 3. Converting currencies without breaking the sum

Converting each person's share separately would round each one on its own, and the converted parts would no longer add up to the converted total. evenup does it in two steps instead:

1. Convert the **total** once, using banker's rounding.
2. Re-allocate that converted total in proportion to the original shares.

So every converted expense still sums to zero across the group.

## 4. Balances and the "before" graph

A person's **net balance** is calculated as:

```
net = paid for the group − own share + payments sent − payments received
```

All the balances add up to exactly zero.

To show how much simplification helps, evenup also builds the **direct-debt graph**. In it, every share is owed straight back to whoever paid it. Where two people owe each other, the two debts cancel and only the difference remains. On the Lisbon example this graph has 15 debts, while the settlement plan has 5 transfers.

## 5. The fewest transfers: an NP-hard problem

Let *n* be the number of people whose balance isn't zero. *n − 1* transfers are always enough: repeatedly let one person settle up with someone else. The question is how far below that you can go.

A set of people whose balances add up to exactly zero can settle among themselves. If the group can be cut into *k* such disjoint zero-sum sets, each set of size *s* needs *s − 1* transfers. Adding those up gives

```
transfers = n − k
```

So **minimising transfers means maximising the number of zero-sum groups.** This problem contains subset-sum (deciding whether one zero-sum subset exists), which makes it NP-hard. No known algorithm solves it in polynomial time.

### Exact solver: dynamic programming over subsets

Number the people with non-zero balances `0 … n−1` and treat every subset as a bitmask.

```
sum[mask] = Σ balances in mask                     (built incrementally from mask & (mask−1))
dp[mask]  = max over i ∈ mask of dp[mask \ {i}]  +  (1 if sum[mask] = 0 else 0)
```

Think of taking people out of the full set one at a time. Every time the remaining set sums to zero, a group has just been completed. `dp[mask]` is the largest number of such moments over all orders of removal, and it equals the largest number of zero-sum groups. To reconstruct the groups, walk back from the full set, always choosing a removal that achieves the optimum. Each time the remaining set sums to zero, close a group.

The solver then settles each group internally. A group of size *s* uses *s − 1* transfers, so the total is exactly *n − k*.

| | |
|---|---|
| Time | O(2ⁿ · n) |
| Memory | 2ⁿ bytes for `dp` + 2ⁿ int64 for `sum` (≈ 9 MB at n = 20) |
| Default limit | n ≤ 20 (`EVENUP_EXACT_LIMIT`, max 22) |

**Correctness is tested, not assumed.** A property test generates hundreds of random small groups in which zero-sum subsets are common. On each one it compares the solver against a brute-force search over every partition. Other properties check that every plan settles every balance exactly, never uses more than *n − 1* transfers, and never uses more than greedy.

### Why not just greedy?

The usual approach, "largest debtor pays largest creditor", is fast but can't see structure. `examples/greedy-trap.evenup` shows this. It holds two circles of friends, {+5, −3, −2} and {+4, −4}. Greedy pairs the €5 creditor with the €4 debtor first, which mixes the two circles and costs **4 transfers**. evenup finds the two groups and needs **3**. Reports show "greedy would need N" whenever this happens.

### Larger groups: hybrid mode

When more than `EVENUP_EXACT_LIMIT` people have a balance, evenup switches to hybrid mode:

1. Pair off people whose balances cancel exactly (+x and −x). Some optimal solution always contains such a pair as its own group.
2. If what's left fits under the limit, solve it exactly. Otherwise, use greedy.

The report marks the plan as `hybrid` rather than "proven minimal".
