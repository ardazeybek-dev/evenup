namespace Evenup

open System

type Person = string

/// How an expense is shared between people.
type Split =
    /// Everyone listed pays the same; an empty list means the whole group.
    | Equal of among: Person list
    /// Proportional to weights, e.g. by nights stayed or room size.
    | Shares of (Person * decimal) list
    /// Percentages that must add up to exactly 100.
    | Percent of (Person * decimal) list
    /// Fixed amounts (minor units, expense currency) that must add up to the total.
    | Exact of (Person * int64) list
    /// Receipt lines; whatever the lines don't cover (tax, tip, service, discount)
    /// is spread in proportion to each person's subtotal.
    | Itemized of Item list

and Item =
    { Label: string
      People: Person list
      Amount: int64 }

/// Who fronted the money.
type Payers =
    | One of Person
    | Many of (Person * int64) list

type Expense =
    { Line: int
      Date: DateOnly option
      Title: string
      Payers: Payers
      Amount: int64
      Currency: string
      Split: Split }

/// A direct transfer that already happened ("alice paid bob 50").
type Payment =
    { Line: int
      Date: DateOnly option
      From: Person
      To: Person
      Amount: int64
      Currency: string }

type Entry =
    | ExpenseEntry of Expense
    | PaymentEntry of Payment

type Ledger =
    { Name: string
      /// Every balance and transfer is expressed in this currency.
      Base: string
      /// 1 unit of the key currency = value units of `Base`.
      Rates: Map<string, decimal>
      /// Declared members. When empty, members are inferred from the entries.
      People: Person list
      Entries: Entry list }

/// A problem tied to a line in the source, reported the way compilers do.
type Diagnostic =
    { Line: int
      Column: int
      Message: string }

    override d.ToString() =
        if d.Line > 0 then sprintf "line %d:%d: %s" d.Line d.Column d.Message else d.Message

/// A money movement between two people, in the base currency.
type Transfer =
    { From: Person
      To: Person
      Amount: int64 }
