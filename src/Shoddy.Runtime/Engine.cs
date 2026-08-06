// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Shoddy.Runtime;

/// <summary>
/// The execution engine woven programs run against: the value stack,
/// every builtin word, record operations, and I/O. Quotation values are
/// CLR closures — a body Action plus a QItem array for sequence ops and
/// printing (Shoddy's lists ARE quotations).
/// </summary>
public sealed partial class Engine
{
    readonly List<Value> Stk = new();
    readonly FileStream?[] files = new FileStream?[16];
    readonly Socket?[] socks = new Socket?[16];   // TCP: connected sockets and listeners share the table
    // The network is a gated capability — off unless the mill was armed
    // with --allow-net (which sets SHODDY_ALLOW_NET=1 in the environment,
    // so a standalone woven exe honors the same switch). Read once at
    // construction: policy is fixed for the life of a run.
    readonly bool netOK = Environment.GetEnvironmentVariable("SHODDY_ALLOW_NET") == "1";
    const int ConnectTimeoutMs = 10_000;          // TCPCONNECT: bounded handshake wait
    const int SendPollMicros = 5_000_000;         // TCPSEND: bounded wait for a full send buffer to drain

    // ---- TLS ---------------------------------------------------------
    // A secured handle keeps its Socket in socks and gains an SslStream
    // here, at the same index. TCPSECURE is gated by the same RequireNet
    // as the rest of the family: it is more network, not a new capability.
    //
    // SECURED MEANS BLOCKING, and that is a deliberate split rather than an
    // oversight. SslStream cannot be driven off a non-blocking socket, and
    // once TLS frames wrap the stream a TCP-level Poll stops telling the
    // truth about plaintext: a partial record makes bytes look ready that
    // decrypt to nothing yet, and a fully buffered record makes plaintext
    // look absent when a Read would return it at once. So on a secured
    // handle TCPRECV blocks until data, EOF or timeout, and TCPPOLL refuses
    // outright rather than answer dishonestly.
    //
    // The two worlds genuinely differ. Nobody polls a TLS socket in a game
    // loop: TLS exists here for request/response at the edges, while the
    // polling contract exists for scribbler windows and game loops that go
    // on using plain sockets on the loopback. Both keep their own truth.
    readonly SslStream?[] tls = new SslStream?[16];
    readonly bool[] tlsEof = new bool[16];        // a Read returned 0 — the peer closed
    // Test hook, not a feature: accept any certificate. Read once at
    // construction like netOK, has no mill flag, and NetTests is its only
    // intended consumer.
    readonly bool tlsInsecure = Environment.GetEnvironmentVariable("SHODDY_TLS_INSECURE") == "1";
    const int TlsHandshakeTimeoutMs = 10_000;     // TCPSECURE: a handshake is a connect
    const int TlsReadTimeoutMs = 30_000;          // TCPRECV secured: a slow API is normal, a hung one must still die
    Random rnd = new();   // reassigned by SEED for reproducible runs
    readonly TextWriter O;
    readonly TextReader In;

    static readonly Encoding Bytes = Encoding.Latin1;   // 1 char = 1 byte
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly string[] progArgs;

    public Engine(TextWriter? output = null, TextReader? input = null, string[]? args = null)
    {
        O = output ?? Console.Out;
        In = input ?? Console.In;
        progArgs = args ?? Array.Empty<string>();
    }

    static ShoddyError Die(int line, string msg) => new(line, msg);

    // ---- value stack --------------------------------------------------

    public int Depth => Stk.Count;

    public void Push(Value v) => Stk.Add(v);

    public Value Pop(int line)
    {
        if (Stk.Count == 0) throw Die(line, "stack underflow");
        Value v = Stk[^1];
        Stk.RemoveAt(Stk.Count - 1);
        return v;
    }

    public void PushNum(double d) => Push(Value.OfNum(d));
    public void PushStr(string s) => Push(Value.OfStr(s));
    public void PushBool(bool b) => Push(Value.OfBool(b));

    public double PopNum(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Num) throw Die(line, $"{who} expects a NUMBER, got {Value.TypeName(v.T)}");
        return v.Num;
    }

    public string PopStr(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Str) throw Die(line, $"{who} expects a STRING, got {Value.TypeName(v.T)}");
        return v.Str!;
    }

    public bool PopBool(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Bool)
            throw Die(line, $"{who} expects a BOOLEAN, got {Value.TypeName(v.T)} (booleans are not numbers in Shoddy)");
        return v.B;
    }

    /* pop a function value, keeping its captured environment (closure) */
    public Value PopFunc(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Quot)
            throw Die(line, $"{who} expects a QUOTATION, got {Value.TypeName(v.T)}");
        return v;
    }

    Value PopListVal(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Quot)
            throw Die(line, $"{who} expects a QUOTATION or LIST, got {Value.TypeName(v.T)}");
        return v;
    }

    /// <summary>For list construction: assert a compiled element left
    /// exactly one value since depth d0, and take it.</summary>
    public Value TakeOne(int d0, int line)
    {
        if (Stk.Count != d0 + 1)
            throw Die(line, "list element must yield exactly one value");
        return Pop(line);
    }

    // ---- quotations ----------------------------------------------------

    /// <summary>Execute a quotation value: its body closure, or (for a
    /// constructed list) push each item.</summary>
    public void CallQuot(Value f)
    {
        if (f.Body != null) { f.Body(); return; }
        foreach (QItem it in f.CItems!)
        {
            if (it.Lit != null) Push(it.Lit);
            else it.Act!();
        }
    }

    /// <summary>Evaluate the k-th item of a quotation-kind sequence;
    /// must yield exactly one value.</summary>
    Value QuotItem(Value q, int k, int line, string who)
    {
        QItem it = q.CItems![k];
        if (it.Lit != null) return it.Lit;
        int d0 = Stk.Count;
        it.Act!();
        if (Stk.Count != d0 + 1)
            throw Die(line, $"{who}: each list item must yield exactly one value");
        return Pop(line);
    }

    /// <summary>Build a new list (quotation-kind sequence) of values.</summary>
    static Value NewValueList(List<Value> vals, int line)
    {
        var items = new QItem[vals.Count];
        for (int k = 0; k < vals.Count; k++) items[k] = QItem.OfValue(vals[k]);
        return Value.OfCQuot(items, items);
    }

    public void PushCQuot(object id, QItem[] items, Action? body) =>
        Push(Value.OfCQuot(id, items, body));

    /// <summary>Push a freshly-constructed list of values — the woven
    /// form of a <c>{ ... }</c> list literal (new identity per run).</summary>
    public void PushList(List<Value> vals, int line) => Push(NewValueList(vals, line));

    // ---- shared semantics ---------------------------------------------

    public static bool EqualValues(Value a, Value b)
    {
        if (a.T != b.T) return false;
        switch (a.T)
        {
            case VType.Num: return a.Num == b.Num;
            case VType.Str: return a.Str == b.Str;
            case VType.Bool: return a.B == b.B;
            case VType.Quot: return ReferenceEquals(a.CId, b.CId);
            case VType.Rec:
                if (!ReferenceEquals(a.RType, b.RType)) return false;
                for (int k = 0; k < a.Elems!.Length; k++)
                    if (!EqualValues(a.Elems[k], b.Elems![k])) return false;
                return true;
            case VType.Arr:
                if (a.Elems!.Length != b.Elems!.Length) return false;
                for (int k = 0; k < a.Elems.Length; k++)
                    if (!EqualValues(a.Elems[k], b.Elems[k])) return false;
                return true;
            case VType.Scribbler:               // opaque mutable reference
                return ReferenceEquals(a.Scribbler, b.Scribbler);
        }
        return false;
    }

    static int SeqLen(Value s) =>
        s.T == VType.Arr ? s.Elems!.Length : s.CItems!.Length;

    Value PopSeq(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Quot && v.T != VType.Arr)
            throw Die(line, $"{who} expects a LIST or ARRAY, got {Value.TypeName(v.T)}");
        return v;
    }

    Value SeqItem(Value s, int k, int line, string who) =>
        s.T == VType.Arr ? s.Elems![k] : QuotItem(s, k, line, who);

    /// <summary>Record construction: pop fields (rightmost on top).</summary>
    public void Ctor(TypeDef t, int line)
    {
        var fields = new Value[t.Fields.Count];
        for (int k = t.Fields.Count - 1; k >= 0; k--)
            fields[k] = Pop(line);
        Push(Value.OfRec(t, fields));
    }

    /// <summary>Field accessor: ( rec -- value ).</summary>
    public void Field(string name, int line)
    {
        Value v = Pop(line);
        if (v.T != VType.Rec)
            throw Die(line, $"{name} expects a RECORD, got {Value.TypeName(v.T)}");
        int fi = v.RType!.FieldIndex(name);
        if (fi < 0)
            throw Die(line, $"{v.RType.Name} has no field {name}");
        Push(v.Elems![fi]);
    }

    /// <summary>WITH: pop field values (in names order, rightmost on top),
    /// then the record; push the functionally-updated record.</summary>
    public void With(int line, string[] names)
    {
        var vals = new Value[names.Length];
        for (int k = names.Length - 1; k >= 0; k--)
            vals[k] = Pop(line);
        Value r = Pop(line);
        if (r.T != VType.Rec)
            throw Die(line, $"WITH expects a RECORD, got {Value.TypeName(r.T)}");
        var fields = (Value[])r.Elems!.Clone();
        for (int k = 0; k < names.Length; k++)
        {
            int fi = r.RType!.FieldIndex(names[k]);
            if (fi < 0)
                throw Die(line, $"{r.RType.Name} has no field {names[k]}");
            fields[fi] = vals[k];
        }
        Push(Value.OfRec(r.RType!, fields));
    }

    public void UnknownWord(string name, int line) =>
        throw Die(line, $"unknown word: {name}");

    /// <summary>Execute a builtin word or die — for woven code.</summary>
    public void Op(string w, int line)
    {
        if (!Builtin(w, line)) UnknownWord(w, line);
    }

    // ---- text files ----

    /// <summary>The reason TRYREADFILE reports inside its Err, as a short
    /// phrase from a closed set. A caller that could act differently on why
    /// it failed is the whole test for Result over Option, so the phrase has
    /// to distinguish the cases a mistyped entry line actually produces —
    /// and it must not be the platform's exception text, which varies by OS
    /// and by locale and would make a golden test unwritable. The directory
    /// check is explicit rather than left to the exception: every platform
    /// reports it as access denied, which is the one wrong answer here,
    /// since a directory is exactly what FILEEXISTS also gets wrong.</summary>
    static string ReadWhy(Exception e, string path) =>
        Directory.Exists(path) ? "IS A DIRECTORY"
        : e is FileNotFoundException or DirectoryNotFoundException ? "NO SUCH FILE"
        : e is UnauthorizedAccessException ? "ACCESS DENIED"
        : "UNREADABLE";

    // ---- binary random-access files ----

    FileStream BinHandle(double h, int line, string w)
    {
        int k = (int)h;
        if (k < 1 || k > files.Length || files[k - 1] == null)
            throw Die(line, $"{w}: bad file handle");
        return files[k - 1]!;
    }

    // ---- TCP/IP sockets ----

    // A socket handle is a 1-based index into socks, exactly like a file
    // handle. A slot holds either a listener (from TCPLISTEN) or a
    // connected socket (from TCPCONNECT / TCPACCEPT); both are raw Sockets,
    // so one table and one validator serve both roles.
    Socket SockHandle(double h, int line, string w)
    {
        int k = (int)h;
        if (k < 1 || k > socks.Length || socks[k - 1] == null)
            throw Die(line, $"{w}: bad socket handle");
        return socks[k - 1]!;
    }

    int SockSlot(int line, string w)
    {
        int slot = Array.IndexOf(socks, null);
        if (slot < 0) throw Die(line, $"{w}: too many open sockets");
        return slot;
    }

    void RequireNet(int line, string w)
    {
        if (!netOK)
            throw Die(line, $"{w}: network is disabled — run the mill with --allow-net");
    }

    /// <summary>All builtin words, dispatched by folded name.
    /// Returns true if the word was handled.</summary>
    public bool Builtin(string w, int line)
    {
        switch (w)
        {
            /* ---- stack shuffling ---- */
            case "DUP": { Value a = Pop(line); Push(a); Push(a); return true; }
            case "DROP": Pop(line); return true;
            case "SWAP": { Value b = Pop(line), a = Pop(line); Push(b); Push(a); return true; }
            case "OVER": { Value b = Pop(line), a = Pop(line); Push(a); Push(b); Push(a); return true; }
            case "ROT": { Value c = Pop(line), b = Pop(line), a = Pop(line); Push(b); Push(c); Push(a); return true; }
            case "NIP": { Value b = Pop(line); Pop(line); Push(b); return true; }
            case "TUCK": { Value b = Pop(line), a = Pop(line); Push(b); Push(a); Push(b); return true; }
            case "DEPTH": PushNum(Stk.Count); return true;

            /* ---- arithmetic ---- */
            case "+": { double b = PopNum(line, w), a = PopNum(line, w); PushNum(a + b); return true; }
            case "-": { double b = PopNum(line, w), a = PopNum(line, w); PushNum(a - b); return true; }
            case "*": { double b = PopNum(line, w), a = PopNum(line, w); PushNum(a * b); return true; }
            case "/":
            {
                double b = PopNum(line, w), a = PopNum(line, w);
                if (b == 0) throw Die(line, "division by zero");
                PushNum(a / b); return true;
            }
            case "MOD":
            {
                double b = PopNum(line, w), a = PopNum(line, w);
                if (b == 0) throw Die(line, "MOD by zero");
                PushNum(a % b); return true;    // C fmod semantics
            }
            case "WRAP":
            {
                // Floored modulo: result carries the divisor's sign, so
                // Wrap(-10, 360) is 350, not -10 — angle and index wrapping.
                double b = PopNum(line, w), a = PopNum(line, w);
                if (b == 0) throw Die(line, "WRAP by zero");
                PushNum(a - b * Math.Floor(a / b)); return true;
            }
            case "NEGATE": PushNum(-PopNum(line, w)); return true;
            case "ABS": PushNum(Math.Abs(PopNum(line, w))); return true;
            case "SGN": { double a = PopNum(line, w); PushNum(a < 0 ? -1 : a > 0 ? 1 : 0); return true; }
            case "MIN": { double b = PopNum(line, w), a = PopNum(line, w); PushNum(a < b ? a : b); return true; }
            case "MAX": { double b = PopNum(line, w), a = PopNum(line, w); PushNum(a > b ? a : b); return true; }
            case "SQR":
            {
                double a = PopNum(line, w);
                if (a < 0) throw Die(line, "SQR of negative number");
                PushNum(Math.Sqrt(a)); return true;
            }
            case "FLOOR": PushNum(Math.Floor(PopNum(line, w))); return true;
            case "CEIL": PushNum(Math.Ceiling(PopNum(line, w))); return true;
            case "ROUND": PushNum(Math.Floor(PopNum(line, w) + 0.5)); return true;   // half-up
            case "FIX": PushNum(Math.Truncate(PopNum(line, w))); return true;   // toward zero, unlike FLOOR
            case "^":
            {
                double b = PopNum(line, w), a = PopNum(line, w);
                double r = Math.Pow(a, b);
                if (!double.IsFinite(r)) throw Die(line, "invalid exponentiation");
                PushNum(r); return true;
            }
            case "SIN": PushNum(Math.Sin(PopNum(line, w))); return true;
            case "COS": PushNum(Math.Cos(PopNum(line, w))); return true;
            case "TAN": PushNum(Math.Tan(PopNum(line, w))); return true;
            case "ATN": PushNum(Math.Atan(PopNum(line, w))); return true;
            case "ATN2": { double x = PopNum(line, w), y = PopNum(line, w); PushNum(Math.Atan2(y, x)); return true; }
            case "ASIN":
            {
                double a = PopNum(line, w);
                if (a < -1 || a > 1) throw Die(line, "ASIN outside [-1, 1]");
                PushNum(Math.Asin(a)); return true;
            }
            case "ACOS":
            {
                double a = PopNum(line, w);
                if (a < -1 || a > 1) throw Die(line, "ACOS outside [-1, 1]");
                PushNum(Math.Acos(a)); return true;
            }
            case "TANH": PushNum(Math.Tanh(PopNum(line, w))); return true;   // saturates to ±1, never overflows
            case "EXP": PushNum(Math.Exp(PopNum(line, w))); return true;
            case "LOG":
            {
                double a = PopNum(line, w);
                if (a <= 0) throw Die(line, "LOG of non-positive number");
                PushNum(Math.Log(a)); return true;
            }
            case "LOG10":
            {
                double a = PopNum(line, w);
                if (a <= 0) throw Die(line, "LOG10 of non-positive number");
                PushNum(Math.Log10(a)); return true;
            }
            case "PI": PushNum(Math.PI); return true;
            case "RND": PushNum(rnd.NextDouble()); return true;   // ( -- x ), in [0,1)
            case "SEED": rnd = new Random((int)PopNum(line, w)); return true;   // ( n -- ) reproducible RND

            /* ---- special functions (stats CDFs; not in System.Math) ---- */
            case "ERF":                         // ( x -- erf(x) ), odd, range (-1,1)
            {
                double x = PopNum(line, w);
                double p = GammaP(0.5, x * x);  // erf(|x|) = P(1/2, x^2)
                PushNum(x >= 0 ? p : -p);
                return true;
            }
            case "GAMMAP":                      // ( a x -- P(a,x) ), regularized lower incomplete gamma
            {
                double x = PopNum(line, w), a = PopNum(line, w);
                if (a <= 0) throw Die(line, "GAMMAP: A must be positive");
                if (x < 0) throw Die(line, "GAMMAP: X must be non-negative");
                PushNum(GammaP(a, x));
                return true;
            }
            case "BETAI":                       // ( a b x -- I_x(a,b) ), regularized incomplete beta
            {
                double x = PopNum(line, w), bb = PopNum(line, w), aa = PopNum(line, w);
                if (aa <= 0 || bb <= 0) throw Die(line, "BETAI: A and B must be positive");
                if (x < 0 || x > 1) throw Die(line, "BETAI: X outside [0, 1]");
                PushNum(BetaI(aa, bb, x));
                return true;
            }

            /* ---- errors and testing ---- */
            case "ERROR": throw Die(line, PopStr(line, w));
            case "ASSERT":                      // ( bool msg -- )
            {
                string msg = PopStr(line, w);
                if (!PopBool(line, w)) throw Die(line, $"ASSERTION FAILED: {msg}");
                return true;
            }
            case "INSTR":                       // ( s sub -- pos ), 0 if absent
            {
                string subs = PopStr(line, w), s = PopStr(line, w);
                if (subs.Length == 0) { PushNum(0); return true; }
                PushNum(s.IndexOf(subs, StringComparison.Ordinal) + 1);
                return true;
            }

            /* ---- comparison ---- */
            case "=":
            case "<>":
            {
                Value b = Pop(line), a = Pop(line);
                bool eq = EqualValues(a, b);
                PushBool(w == "=" ? eq : !eq);
                return true;
            }
            case "<":
            case ">":
            case "<=":
            case ">=":
            {
                Value b = Pop(line), a = Pop(line);
                double c;
                if (a.T == VType.Num && b.T == VType.Num) c = a.Num - b.Num;
                else if (a.T == VType.Str && b.T == VType.Str)
                    c = string.CompareOrdinal(a.Str, b.Str);
                else throw Die(line, $"{w} expects two NUMBERs or two STRINGs");
                PushBool(w switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 });
                return true;
            }

            /* ---- logic (strict postfix words; the expression dialect
             * compiles AND/OR to short-circuiting conditionals) ---- */
            case "AND": { bool b = PopBool(line, w), a = PopBool(line, w); PushBool(a && b); return true; }
            case "OR": { bool b = PopBool(line, w), a = PopBool(line, w); PushBool(a || b); return true; }
            case "NOT": PushBool(!PopBool(line, w)); return true;
            case "TRUE": PushBool(true); return true;
            case "FALSE": PushBool(false); return true;

            /* ---- strings ---- */
            case "&":
            {
                string b = PopStr(line, w), a = PopStr(line, w);
                PushStr(a + b); return true;
            }
            case "LEN": PushNum(PopStr(line, w).Length); return true;
            case "STR": PushStr(Format.Num(PopNum(line, w))); return true;
            case "VAL":
            {
                string s = PopStr(line, w);
                if (!Strtod(s, out double v)) throw Die(line, $"VAL: '{s}' is not a number");
                PushNum(v); return true;
            }
            case "ISNUMERIC":                   // ( s -- bool ), true iff VAL would succeed
                PushBool(Strtod(PopStr(line, w), out _)); return true;
            case "VALOR":                       // ( s fallback -- n ), total VAL
            {
                double d = PopNum(line, w);
                string s = PopStr(line, w);
                PushNum(Strtod(s, out double v) ? v : d); return true;
            }
            case "LEFT":
            {
                int n = (int)PopNum(line, w);
                string s = PopStr(line, w);
                n = Math.Clamp(n, 0, s.Length);
                PushStr(s[..n]); return true;
            }
            case "RIGHT":
            {
                int n = (int)PopNum(line, w);
                string s = PopStr(line, w);
                n = Math.Clamp(n, 0, s.Length);
                PushStr(s[(s.Length - n)..]); return true;
            }
            case "MID":                         // ( s start len -- s' ), 1-based
            {
                int n = (int)PopNum(line, w);
                int start = (int)PopNum(line, w);
                string s = PopStr(line, w);
                if (start < 1) start = 1;
                if (start > s.Length) { PushStr(""); return true; }
                if (n < 0) n = 0;
                if (start - 1 + n > s.Length) n = s.Length - (start - 1);
                PushStr(s.Substring(start - 1, n)); return true;
            }
            case "CHR":
            {
                int c = (int)PopNum(line, w);
                PushStr(c == 0 ? "" : ((char)c).ToString());   // NUL ends a C string
                return true;
            }
            case "ASC":
            {
                string s = PopStr(line, w);
                if (s.Length == 0) throw Die(line, "ASC of empty string");
                PushNum(s[0]); return true;
            }
            case "UPPER":
            case "LOWER":
            {
                string s = PopStr(line, w);
                var sb = new StringBuilder(s.Length);
                foreach (char ch in s)                        // ASCII, like C's toupper
                    sb.Append(w == "UPPER"
                        ? ch is >= 'a' and <= 'z' ? (char)(ch - 32) : ch
                        : ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch);
                PushStr(sb.ToString()); return true;
            }

            /* ---- I/O ---- */
            case "PRINT":
                Printer.PrintValue(O, Pop(line));
                O.Write('\n');
                return true;
            case "READFILE":                    // ( path -- s ) whole file
            {
                string path = PopStr(line, w);
                byte[] buf;
                try { buf = File.ReadAllBytes(path); }
                catch (Exception) { throw Die(line, $"READFILE: cannot open '{path}'"); }
                PushStr(Bytes.GetString(buf));
                return true;
            }
            case "TRYREADFILE":                 // ( path -- Result )
            {
                // READFILE with the failure REPORTED rather than fatal, and
                // the twin TRYWRITEFILE has always had. Whole-file reads were
                // the one I/O family with no guarded form: writes answer
                // through TRYWRITEFILE, binary reads pre-flight through
                // BSIZE, but a mistyped path handed to READFILE ends the run,
                // and FILEEXISTS cannot stand in for the question -- a
                // directory reports false and an unreadable existing file
                // reports true.
                //
                // It answers the LANGUAGE's own Result rather than a Boolean
                // or a flat Array, because unlike TRYWRITEFILE it has a
                // payload to carry on success and a reason worth telling
                // apart on failure. Ok(text) or Err(why, 0) -- At is 0
                // because a failure to open has no position in the file.
                // Prelude's TypeDefs are the one shared instance, so the
                // record this pushes matches a Case Ok in any weave (the
                // usual "a builtin cannot reach a TypeDef" bar applies to
                // MACHINE types, not to the four the language predeclares).
                string path = PopStr(line, w);
                byte[] buf;
                try { buf = File.ReadAllBytes(path); }
                catch (Exception e) when (e is not ShoddyError)
                {
                    Push(Value.OfRec(Prelude.Err, new[]
                    {
                        Value.OfStr($"CANNOT READ '{path}' ({ReadWhy(e, path)})"),
                        Value.OfNum(0),
                    }));
                    return true;
                }
                Push(Value.OfRec(Prelude.Ok, new[] { Value.OfStr(Bytes.GetString(buf)) }));
                return true;
            }
            case "WRITEFILE":
            case "APPENDFILE":
            {
                string s = PopStr(line, w);     // ( path s -- )
                string path = PopStr(line, w);
                try
                {
                    using var f = new FileStream(path,
                        w == "WRITEFILE" ? FileMode.Create : FileMode.Append, FileAccess.Write);
                    f.Write(Bytes.GetBytes(s));
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    throw Die(line, $"{w}: cannot open '{path}'");
                }
                return true;
            }
            case "TRYWRITEFILE":                // ( path s -- ok )
            {
                // WRITEFILE with the failure reported rather than fatal.
                // Shoddy has no catchable errors, so without this "can this
                // path be written?" is an unaskable question -- FILEEXISTS
                // cannot answer it, since a directory reports false and an
                // unwritable existing file reports true.
                string s = PopStr(line, w);
                string path = PopStr(line, w);
                try
                {
                    using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
                    f.Write(Bytes.GetBytes(s));
                    PushBool(true);
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    PushBool(false);
                }
                return true;
            }
            case "FILEEXISTS":                  // ( path -- bool )
                PushBool(File.Exists(PopStr(line, w)));
                return true;
            case "DELETEFILE":                  // ( path -- )
            {
                string path = PopStr(line, w);
                try
                {
                    if (!File.Exists(path)) throw new IOException();
                    File.Delete(path);
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    throw Die(line, $"DELETEFILE: cannot delete '{path}'");
                }
                return true;
            }

            /* binary random-access: handles are NUMBERs, positions are
             * 1-based bytes, GET/PUT advance. NUMBER = 8-byte native-endian
             * double, BOOLEAN = 1 byte, strings = fixed zero-padded fields. */
            case "BOPEN":                       // ( path -- handle )
            {
                string path = PopStr(line, w);
                int slot = Array.IndexOf(files, null);
                if (slot < 0) throw Die(line, "BOPEN: too many open files");
                try
                {
                    files[slot] = new FileStream(path, FileMode.OpenOrCreate,
                                                 FileAccess.ReadWrite);
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    throw Die(line, $"BOPEN: cannot open '{path}'");
                }
                PushNum(slot + 1);
                return true;
            }
            case "BCLOSE":                      // ( h -- )
            {
                double h = PopNum(line, w);
                BinHandle(h, line, w).Dispose();
                files[(int)h - 1] = null;
                return true;
            }
            case "SEEK":                        // ( h pos -- ), 1-based bytes
            {
                double pos = PopNum(line, w);
                FileStream f = BinHandle(PopNum(line, w), line, w);
                if (pos < 1) throw Die(line, "SEEK: position must be >= 1");
                f.Position = (long)pos - 1;
                return true;
            }
            case "BPOS":                        // ( h -- pos )
                PushNum(BinHandle(PopNum(line, w), line, w).Position + 1);
                return true;
            case "BSIZE":                       // ( h -- bytes )
                PushNum(BinHandle(PopNum(line, w), line, w).Length);
                return true;
            case "PUTNUM":                      // ( h n -- ), 8 bytes
            {
                double d = PopNum(line, w);
                FileStream f = BinHandle(PopNum(line, w), line, w);
                f.Write(BitConverter.GetBytes(d));
                return true;
            }
            case "GETNUM":                      // ( h -- n )
            {
                FileStream f = BinHandle(PopNum(line, w), line, w);
                var buf = new byte[8];
                if (f.ReadAtLeast(buf, 8, false) != 8)
                    throw Die(line, "GETNUM: read past end of file");
                PushNum(BitConverter.ToDouble(buf));
                return true;
            }
            case "PUTBOOL":                     // ( h b -- ), 1 byte
            {
                byte c = PopBool(line, w) ? (byte)1 : (byte)0;
                FileStream f = BinHandle(PopNum(line, w), line, w);
                f.WriteByte(c);
                return true;
            }
            case "GETBOOL":                     // ( h -- b )
            {
                FileStream f = BinHandle(PopNum(line, w), line, w);
                int c = f.ReadByte();
                if (c < 0) throw Die(line, "GETBOOL: read past end of file");
                PushBool(c != 0);
                return true;
            }
            case "PUTSTR":                      // ( h s len -- ), fixed field
            {
                int len = (int)PopNum(line, w);
                string s = PopStr(line, w);
                FileStream f = BinHandle(PopNum(line, w), line, w);
                if (len < 1) throw Die(line, "PUTSTR: field length must be >= 1");
                byte[] sb = Bytes.GetBytes(s);
                if (sb.Length > len)
                    throw Die(line, $"PUTSTR: string of {sb.Length} bytes exceeds the {len}-byte field");
                f.Write(sb);
                for (int k = sb.Length; k < len; k++) f.WriteByte(0);
                return true;
            }
            case "GETSTR":                      // ( h len -- s ), strips padding
            {
                int len = (int)PopNum(line, w);
                FileStream f = BinHandle(PopNum(line, w), line, w);
                if (len < 1) throw Die(line, "GETSTR: field length must be >= 1");
                var buf = new byte[len];
                if (f.ReadAtLeast(buf, len, false) != len)
                    throw Die(line, "GETSTR: read past end of file");
                string s = Bytes.GetString(buf);
                int z = s.IndexOf('\0');        // C strings end at the first NUL
                PushStr(z < 0 ? s : s[..z]);
                return true;
            }
            /* ---- TCP/IP sockets ----------------------------------------
             * Handles are NUMBERs, like file handles. Payloads cross as
             * STRINGs through the Latin1 Bytes codec (1 char = 1 byte), the
             * same binary-safe convention ReadFile and PutStr use. Every
             * readiness op is NON-BLOCKING: TCPRECV yields "" when no data
             * has arrived, TCPACCEPT yields 0 when no client is waiting, so
             * a program polls (optionally around Sleep) and never freezes a
             * scribbler window or a debug session. TCPRECV returns "" for
             * both "nothing yet" and "peer closed" — TCPEOF tells them
             * apart. The whole family is gated behind --allow-net. */
            case "TCPCONNECT":                  // ( host port -- h )
            {
                RequireNet(line, w);
                int port = (int)PopNum(line, w);
                string host = PopStr(line, w);
                int slot = SockSlot(line, w);
                Socket sk = new(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // Bounded handshake: a dead host must not hang the run.
                    if (!sk.ConnectAsync(host, port).Wait(ConnectTimeoutMs))
                    {
                        sk.Dispose();
                        throw Die(line, $"TCPCONNECT: '{host}:{port}' timed out");
                    }
                    sk.Blocking = false;        // every op hereafter returns at once
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    sk.Dispose();
                    throw Die(line, $"TCPCONNECT: cannot reach '{host}:{port}'");
                }
                socks[slot] = sk;
                PushNum(slot + 1);
                return true;
            }
            case "TCPLISTEN":                   // ( host port -- h ), host is an IP literal
            {
                RequireNet(line, w);
                int port = (int)PopNum(line, w);
                string host = PopStr(line, w);
                int slot = SockSlot(line, w);
                Socket sk = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (!IPAddress.TryParse(host, out IPAddress? ip))
                        throw Die(line, $"TCPLISTEN: '{host}' is not an IP address (try \"127.0.0.1\")");
                    sk.Bind(new IPEndPoint(ip, port));
                    sk.Listen(16);
                    sk.Blocking = false;
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    sk.Dispose();
                    throw Die(line, $"TCPLISTEN: cannot bind {host}:{port}");
                }
                socks[slot] = sk;
                PushNum(slot + 1);
                return true;
            }
            case "TCPACCEPT":                   // ( h -- connH ), 0 when none pending
            {
                RequireNet(line, w);
                Socket srv = SockHandle(PopNum(line, w), line, w);
                Socket? c = null;
                try
                {
                    if (srv.Poll(0, SelectMode.SelectRead))   // a client is waiting?
                        c = srv.Accept();
                }
                catch (SocketException) { c = null; }
                if (c == null) { PushNum(0); return true; }
                int slot = Array.IndexOf(socks, null);
                if (slot < 0) { c.Dispose(); throw Die(line, "TCPACCEPT: too many open sockets"); }
                c.Blocking = false;
                socks[slot] = c;
                PushNum(slot + 1);
                return true;
            }
            case "TCPSECURE":                   // ( h host -- )
            {
                // host drives BOTH the SNI extension and certificate name
                // validation, so it is a parameter rather than something
                // remembered from TCPCONNECT — which may have been given a
                // bare IP, and an IP is not a name a certificate can match.
                RequireNet(line, w);
                string host = PopStr(line, w);
                int k = (int)PopNum(line, w);
                Socket sk = SockHandle(k, line, w);
                if (tls[k - 1] != null) throw Die(line, "TCPSECURE: already secured");
                if (!sk.Connected) throw Die(line, "TCPSECURE: handle is a listener");
                SslStream? ss = null;
                try
                {
                    sk.Blocking = true;         // SslStream cannot drive a non-blocking socket
                    ss = new SslStream(new NetworkStream(sk, ownsSocket: false), false,
                        (sender, cert, chain, errors) =>
                            tlsInsecure || errors == SslPolicyErrors.None);
                    if (!ss.AuthenticateAsClientAsync(host).Wait(TlsHandshakeTimeoutMs))
                        throw new TimeoutException("handshake timed out");
                    ss.ReadTimeout = TlsReadTimeoutMs;
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    // A connection whose handshake failed has no useful
                    // half-open state to offer a language with no catchable
                    // errors, so the handle dies with it.
                    ss?.Dispose();
                    sk.Dispose();
                    socks[k - 1] = null;
                    tls[k - 1] = null;
                    tlsEof[k - 1] = false;
                    Exception inner = e is AggregateException ag ? ag.GetBaseException() : e;
                    throw Die(line, $"TCPSECURE: handshake with '{host}' failed — {inner.Message}");
                }
                tls[k - 1] = ss;
                tlsEof[k - 1] = false;
                return true;
            }
            case "TCPSEND":                     // ( h s -- )
            {
                RequireNet(line, w);
                string s = PopStr(line, w);
                int sh = (int)PopNum(line, w);
                Socket sk = SockHandle(sh, line, w);
                if (tls[sh - 1] is SslStream sec)
                {
                    // Blocking, and naturally bounded by TCP itself.
                    byte[] enc = Bytes.GetBytes(s);
                    try { sec.Write(enc, 0, enc.Length); sec.Flush(); }
                    catch (Exception e) when (e is not ShoddyError)
                    {
                        throw Die(line, $"TCPSEND: {e.Message}");
                    }
                    return true;
                }
                byte[] data = Bytes.GetBytes(s);
                int sent = 0;
                try
                {
                    while (sent < data.Length)
                    {
                        int n = sk.Send(data, sent, data.Length - sent,
                                        SocketFlags.None, out SocketError err);
                        if (n > 0) { sent += n; continue; }
                        if (err == SocketError.WouldBlock)
                        {
                            // Send buffer full: wait (bounded) for it to drain
                            // rather than spin or drop bytes.
                            if (!sk.Poll(SendPollMicros, SelectMode.SelectWrite))
                                throw Die(line, "TCPSEND: send timed out");
                            continue;
                        }
                        throw Die(line, $"TCPSEND: {err}");
                    }
                }
                catch (SocketException e)
                {
                    throw Die(line, $"TCPSEND: {e.SocketErrorCode}");
                }
                return true;
            }
            case "TCPRECV":                     // ( h max -- s ), "" when nothing pending
            {
                RequireNet(line, w);
                int max = (int)PopNum(line, w);
                int rh = (int)PopNum(line, w);
                Socket sk = SockHandle(rh, line, w);
                if (max < 1) throw Die(line, "TCPRECV: byte count must be >= 1");
                if (tls[rh - 1] is SslStream sec)
                {
                    // Blocks until data, EOF or the read timeout. "" means
                    // EOF here and ONLY EOF — there is no "nothing yet"
                    // answer to give once records are in the way.
                    var sbuf = new byte[max];
                    int sn;
                    try { sn = sec.Read(sbuf, 0, max); }
                    catch (IOException) { throw Die(line, "TCPRECV: secure read timed out"); }
                    catch (Exception e) when (e is not ShoddyError)
                    {
                        throw Die(line, $"TCPRECV: {e.Message}");
                    }
                    if (sn == 0) tlsEof[rh - 1] = true;
                    PushStr(Bytes.GetString(sbuf, 0, sn));
                    return true;
                }
                var buf = new byte[max];
                int n;
                try
                {
                    n = sk.Receive(buf, 0, max, SocketFlags.None, out SocketError err);
                    if (err == SocketError.WouldBlock) { PushStr(""); return true; }
                    if (err != SocketError.Success) throw Die(line, $"TCPRECV: {err}");
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
                {
                    PushStr(""); return true;   // no data available right now
                }
                // n == 0 is a clean peer close: also "", disambiguated by TCPEOF
                PushStr(Bytes.GetString(buf, 0, n));
                return true;
            }
            case "TCPEOF":                      // ( h -- bool ), has the peer closed?
            {
                RequireNet(line, w);
                int eh = (int)PopNum(line, w);
                Socket sk = SockHandle(eh, line, w);
                if (tls[eh - 1] != null)
                {
                    // Poll cannot see through the records, so the flag a
                    // zero-length secure read left behind is the only truth.
                    PushBool(tlsEof[eh - 1]);
                    return true;
                }
                bool closed;
                // Readable with nothing buffered is the standard closed signal.
                try { closed = sk.Poll(0, SelectMode.SelectRead) && sk.Available == 0; }
                catch (SocketException) { closed = true; }
                PushBool(closed);
                return true;
            }
            case "TCPPOLL":                     // ( h -- bool ), would recv/accept find something now?
            {
                RequireNet(line, w);
                int ph = (int)PopNum(line, w);
                Socket sk = SockHandle(ph, line, w);
                // It cannot answer honestly on a secured handle, so it
                // refuses rather than lies. Read the socket instead: a
                // secured TCPRECV blocks until there is an answer.
                if (tls[ph - 1] != null)
                    throw Die(line, "TCPPOLL: not meaningful on a secured connection");
                bool ready;
                try { ready = sk.Poll(0, SelectMode.SelectRead); }
                catch (SocketException) { ready = true; }
                PushBool(ready);
                return true;
            }
            case "TCPPEER":                     // ( h -- s ), remote "ip:port" or ""
            {
                RequireNet(line, w);
                Socket sk = SockHandle(PopNum(line, w), line, w);
                string who;
                try { who = sk.RemoteEndPoint?.ToString() ?? ""; }
                catch (SocketException) { who = ""; }
                PushStr(who);
                return true;
            }
            case "TCPCLOSE":                    // ( h -- )
            {
                RequireNet(line, w);
                int k = (int)PopNum(line, w);
                Socket sk = SockHandle(k, line, w);
                // The stream first: disposing it sends the TLS close-notify
                // the peer is entitled to before the socket goes away.
                if (tls[k - 1] is SslStream sec)
                {
                    try { sec.Dispose(); } catch (Exception e) when (e is not ShoddyError) { }
                    tls[k - 1] = null;
                }
                tlsEof[k - 1] = false;
                try { sk.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
                sk.Dispose();
                socks[k - 1] = null;
                return true;
            }
            case "INPUT":                       // ( prompt -- s )
            {
                // The console and a scribbler window are separate input
                // channels routed by OS focus; reading the console while a
                // window is up silently hangs, so it is refused instead.
                if (Volatile.Read(ref ScribblerRegistry.OpenCount) > 0)
                    throw Die(line, "Input: cannot read the console while a scribbler window is open" +
                                    " — read keystrokes with ScribblerWait or ScribblerPoll.");
                O.Write(PopStr(line, w));
                O.Flush();
                PushStr(In.ReadLine() ?? "");
                return true;
            }
            case "INPUTLINE":                   // ( prompt -- [atEof, text] )
            {
                // Like INPUT, but says whether the read hit end of stream.
                // INPUT collapses EOF and a blank line into "", so a program
                // reading a redirected script can either reprompt on a blank
                // line or terminate at EOF, never both. A flat Array carrier,
                // not a record: a builtin cannot reach a TypeDef, so lifting
                // this into a sum type is the Shoddy wrapper's job.
                if (Volatile.Read(ref ScribblerRegistry.OpenCount) > 0)
                    throw Die(line, "InputLine: cannot read the console while a scribbler window is open" +
                                    " — read keystrokes with ScribblerWait or ScribblerPoll.");
                O.Write(PopStr(line, w));
                O.Flush();
                string? got = In.ReadLine();
                Push(Value.OfArr(new[]
                {
                    Value.OfBool(got == null),
                    Value.OfStr(got ?? ""),
                }));
                return true;
            }
            case "INKEY":                       // ( -- s ) one pending keystroke, "" if none
            {
                // Non-blocking and unechoed, unlike INPUT: returns at once
                // with "" when nothing is pending, so a game loop can poll
                // it between frames. Arrow keys and PF1-4 arrive as their
                // VT100 application-mode sequences (ESC OA .. ESC OS), the
                // exact strings machines/vt100.shoddy's EvalKey classifies.
                if (Volatile.Read(ref ScribblerRegistry.OpenCount) > 0)
                    throw Die(line, "InKey: cannot read the console while a scribbler window is open" +
                                    " — read keystrokes with ScribblerWait or ScribblerPoll.");
                if (!ReferenceEquals(In, Console.In) || Console.IsInputRedirected)
                {
                    // Redirected or test-supplied input: consume one pending
                    // character; "" at end of input. Keeps INKEY testable
                    // headless (feed a StringReader) and sane under pipes.
                    PushStr(In.Peek() < 0 ? "" : ((char)In.Read()).ToString());
                    return true;
                }
                if (!Console.KeyAvailable) { PushStr(""); return true; }
                ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                PushStr(k.Key switch
                {
                    ConsoleKey.UpArrow => "\x1bOA",
                    ConsoleKey.DownArrow => "\x1bOB",
                    ConsoleKey.RightArrow => "\x1bOC",
                    ConsoleKey.LeftArrow => "\x1bOD",
                    ConsoleKey.F1 => "\x1bOP",
                    ConsoleKey.F2 => "\x1bOQ",
                    ConsoleKey.F3 => "\x1bOR",
                    ConsoleKey.F4 => "\x1bOS",
                    _ => k.KeyChar == '\0' ? "" : k.KeyChar.ToString(),
                });
                return true;
            }
            case "ARGS":                        // ( -- [args] ) program arguments
            {
                var vals = new List<Value>(progArgs.Length);
                foreach (string a in progArgs) vals.Add(Value.OfStr(a));
                Push(NewValueList(vals, line));
                return true;
            }

            /* ---- combinators ---- */
            case "CALL":
                CallQuot(PopFunc(line, w));
                return true;
            case "IFTE":
            {
                Value fe = PopFunc(line, w), te = PopFunc(line, w);
                bool c = PopBool(line, w);
                CallQuot(c ? te : fe);
                return true;
            }
            case "MAP":                         // result has the input's kind
            {
                Value f = PopFunc(line, w);
                Value l = PopSeq(line, w);
                int n = SeqLen(l);
                var resl = l.T == VType.Quot ? new List<Value>() : null;
                var resa = l.T == VType.Arr ? new Value[n] : null;
                for (int k = 0; k < n; k++)
                {
                    Push(SeqItem(l, k, line, w));
                    int d0 = Stk.Count - 1;
                    CallQuot(f);
                    if (Stk.Count != d0 + 1)
                        throw Die(line, "MAP quotation must leave exactly one value");
                    if (resa != null) resa[k] = Pop(line);
                    else resl!.Add(Pop(line));
                }
                if (resa != null) Push(Value.OfArr(resa)); else Push(NewValueList(resl!, line));
                return true;
            }
            case "FILTER":
            {
                Value f = PopFunc(line, w);
                Value l = PopSeq(line, w);
                int n = SeqLen(l);
                var res = new List<Value>();
                for (int k = 0; k < n; k++)
                {
                    Value item = SeqItem(l, k, line, w);
                    Push(item);
                    int d0 = Stk.Count - 1;
                    CallQuot(f);
                    if (Stk.Count != d0 + 1)
                        throw Die(line, "FILTER quotation must leave exactly one value");
                    if (PopBool(line, w)) res.Add(item);
                }
                if (l.T == VType.Arr) Push(Value.OfArr(res.ToArray()));
                else Push(NewValueList(res, line));
                return true;
            }
            case "FOLD":                        // ( seq acc f -- result )
            {
                Value f = PopFunc(line, w);
                Value acc = Pop(line);
                Value l = PopSeq(line, w);
                Push(acc);
                int n = SeqLen(l);
                for (int k = 0; k < n; k++)
                {
                    Push(SeqItem(l, k, line, w));
                    CallQuot(f);
                }
                return true;
            }
            case "EACH":
            {
                Value f = PopFunc(line, w);
                Value l = PopSeq(line, w);
                int n = SeqLen(l);
                for (int k = 0; k < n; k++)
                {
                    Push(SeqItem(l, k, line, w));
                    CallQuot(f);
                }
                return true;
            }
            case "TIMES":
            {
                Value f = PopFunc(line, w);
                int n = (int)PopNum(line, w);
                for (int k = 0; k < n; k++)
                    CallQuot(f);
                return true;
            }
            case "RANGE":                       // ( a b -- [a..b] )
            {
                double b = PopNum(line, w), a = PopNum(line, w);
                var res = new List<Value>();
                for (double x = a; x <= b; x += 1)
                    res.Add(Value.OfNum(x));
                Push(NewValueList(res, line));
                return true;
            }
            case "LENGTH": PushNum(SeqLen(PopSeq(line, w))); return true;
            case "REVERSE":
            {
                Value l = PopSeq(line, w);
                int n = SeqLen(l);
                if (l.T == VType.Arr)
                {
                    var res = new Value[n];
                    for (int k = 0; k < n; k++) res[k] = l.Elems![n - 1 - k];
                    Push(Value.OfArr(res));
                }
                else                            // share items, reversed
                {
                    var res = new QItem[n];
                    for (int k = 0; k < n; k++) res[k] = l.CItems![n - 1 - k];
                    Push(Value.OfCQuot(res, res));
                }
                return true;
            }
            case "SORT":                        // ascending; result has the input's kind
            {
                Value l = PopSeq(line, w);
                int n = SeqLen(l);
                var vals = new Value[n];
                bool nums = true, strs = true;
                for (int k = 0; k < n; k++)
                {
                    vals[k] = SeqItem(l, k, line, w);
                    nums &= vals[k].T == VType.Num;
                    strs &= vals[k].T == VType.Str;
                }
                if (!nums && !strs)
                    throw Die(line, "SORT expects all NUMBERs or all STRINGs");
                if (nums) Array.Sort(vals, (a, b) => a.Num.CompareTo(b.Num));
                else Array.Sort(vals, (a, b) => string.CompareOrdinal(a.Str, b.Str));
                if (l.T == VType.Arr) Push(Value.OfArr(vals));
                else Push(NewValueList(new List<Value>(vals), line));
                return true;
            }
            case "CONCAT":
            {
                Value b = PopSeq(line, w), a = PopSeq(line, w);
                if (a.T != b.T)
                    throw Die(line, "CONCAT expects two LISTs or two ARRAYs");
                if (a.T == VType.Arr)
                {
                    var res = new Value[a.Elems!.Length + b.Elems!.Length];
                    a.Elems.CopyTo(res, 0);
                    b.Elems.CopyTo(res, a.Elems.Length);
                    Push(Value.OfArr(res));
                }
                else                            // share items
                {
                    var res = new QItem[a.CItems!.Length + b.CItems!.Length];
                    a.CItems.CopyTo(res, 0);
                    b.CItems.CopyTo(res, a.CItems.Length);
                    Push(Value.OfCQuot(res, res));
                }
                return true;
            }

            /* ---- list / array primitives ---- */
            case "ISEMPTY": PushBool(SeqLen(PopSeq(line, w)) == 0); return true;
            case "FIRST":
            {
                Value l = PopSeq(line, w);
                if (SeqLen(l) == 0) throw Die(line, "FIRST of empty sequence");
                Push(SeqItem(l, 0, line, w));
                return true;
            }
            case "NTH":                         // ( seq k -- v ), 1-based
            {
                int k = (int)PopNum(line, w);
                Value l = PopSeq(line, w);
                if (k < 1 || k > SeqLen(l))
                    throw Die(line, $"NTH: index {k} out of range 1..{SeqLen(l)}");
                Push(SeqItem(l, k - 1, line, w));
                return true;
            }
            case "SETNTH":                      // ( seq k v -- seq' ), functional
            {
                Value nv = Pop(line);
                int k = (int)PopNum(line, w);
                Value l = PopSeq(line, w);
                int n = SeqLen(l);
                if (k < 1 || k > n)
                    throw Die(line, $"SETNTH: index {k} out of range 1..{n}");
                if (l.T == VType.Arr)
                {
                    var res = (Value[])l.Elems!.Clone();
                    res[k - 1] = nv;
                    Push(Value.OfArr(res));
                }
                else                            // share items
                {
                    var res = (QItem[])l.CItems!.Clone();
                    res[k - 1] = QItem.OfValue(nv);
                    Push(Value.OfCQuot(res, res));
                }
                return true;
            }
            case "DIM":                         // ( n init -- arr )
            {
                Value init = Pop(line);
                int n = (int)PopNum(line, w);
                if (n < 0) throw Die(line, "DIM: negative size");
                var res = new Value[n];
                Array.Fill(res, init);
                Push(Value.OfArr(res));
                return true;
            }
            case "TOARRAY":
            {
                Value l = PopSeq(line, w);
                if (l.T == VType.Arr) { Push(l); return true; }
                var res = new Value[SeqLen(l)];
                for (int k = 0; k < res.Length; k++)
                    res[k] = QuotItem(l, k, line, w);
                Push(Value.OfArr(res));
                return true;
            }
            case "TOLIST":
            {
                Value l = PopSeq(line, w);
                if (l.T == VType.Quot) { Push(l); return true; }
                Push(NewValueList(new List<Value>(l.Elems!), line));
                return true;
            }
            case "REST":
            {
                Value l = PopListVal(line, w);
                if (SeqLen(l) == 0) throw Die(line, "REST of empty list");
                var res = l.CItems![1..];       // share items
                Push(Value.OfCQuot(res, res));
                return true;
            }
            case "PREPEND":                     // ( v [l] -- [v ...l] )
            {
                Value l = PopListVal(line, w);
                Value v = Pop(line);
                var res = new QItem[l.CItems!.Length + 1];   // share items
                res[0] = QItem.OfValue(v);
                l.CItems.CopyTo(res, 1);
                Push(Value.OfCQuot(res, res));
                return true;
            }

            /* ---- timing (no window required) ----
               TICKS is for measuring (frame pacing, deltas) and is the only
               clock animation may use; CLOCK is for stamping (logs, file
               names) and jumps whenever the OS adjusts system time. */
            case "TICKS":                       // ( -- n ) monotonic ms, fractional
                PushNum(Ticker.Now);
                return true;
            case "SLEEP":                       // ( ms -- ) yield the calling thread
            {
                double ms = PopNum(line, w);
                if (ms > 0) Thread.Sleep((int)Math.Min(ms, int.MaxValue));
                return true;
            }
            case "CLOCK":                       // ( -- arr ) wall clock, 7 fields
            {
                DateTime t = DateTime.Now;
                Push(Value.OfArr(new[]
                {
                    Value.OfNum(t.Year), Value.OfNum(t.Month), Value.OfNum(t.Day),
                    Value.OfNum(t.Hour), Value.OfNum(t.Minute), Value.OfNum(t.Second),
                    Value.OfNum(t.Millisecond),
                }));
                return true;
            }

            /* ---- scribblers ----
               Every word leaves exactly one value (surface calls have no
               arity check, and a word leaving two strands one per call
               site); mutators return the scribbler so Let sc = ... reads
               functionally even though the handle mutates in place. */
            case "SCRIBBLEROPEN":               // ( width height -- scribbler )
            {
                int hgt = (int)PopNum(line, w), wid = (int)PopNum(line, w);
                Func<int, int, ScribblerHandle>? create = ScribblerRegistry.CreateScribbler;
                if (create == null)
                    throw Die(line, "ScribblerOpen: no window backend — scribbler programs require `mill run`");
                if (wid < 1 || hgt < 1)
                    throw Die(line, $"ScribblerOpen: size must be at least 1x1, got {wid}x{hgt}");
                ScribblerHandle h = create(wid, hgt);    // blocks until the window exists
                h.Width = wid; h.Height = hgt;
                if (h.Pixels.Length != wid * hgt * 4) h.Pixels = new byte[wid * hgt * 4];
                Interlocked.Increment(ref ScribblerRegistry.OpenCount);
                Push(Value.OfScribbler(h));
                return true;
            }
            case "SCRIBBLERPIXEL":              // ( scribbler x y r g b -- scribbler )
            {
                byte b = Chan(PopNum(line, w)), g = Chan(PopNum(line, w)), r = Chan(PopNum(line, w));
                int y = (int)PopNum(line, w), x = (int)PopNum(line, w);
                Value v = PopScrib(line, w);
                v.Scribbler!.SetPixelClamped(x, y, r, g, b);
                Push(v);
                return true;
            }
            case "SCRIBBLERFILL":               // ( scribbler r g b -- scribbler )
            {
                byte b = Chan(PopNum(line, w)), g = Chan(PopNum(line, w)), r = Chan(PopNum(line, w));
                Value v = PopScrib(line, w);
                byte[] px = v.Scribbler!.Pixels;
                for (int i = 0; i < px.Length; i += 4)
                {
                    px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
                }
                Push(v);
                return true;
            }
            case "SCRIBBLERTEXT":               // ( scribbler x y scale r g b text -- scribbler )
            {
                string text = PopStr(line, w);
                byte b = Chan(PopNum(line, w)), g = Chan(PopNum(line, w)), r = Chan(PopNum(line, w));
                int scale = (int)PopNum(line, w);
                int y = (int)PopNum(line, w), x = (int)PopNum(line, w);
                Value v = PopScrib(line, w);
                Font8x8.DrawText(v.Scribbler!, text, x, y, scale, r, g, b);
                Push(v);
                return true;
            }
            case "SCRIBBLERGETPIXEL":           // ( scribbler x y -- arr ) r,g,b; OOB reads 0,0,0
            {
                int y = (int)PopNum(line, w), x = (int)PopNum(line, w);
                ScribblerHandle h = PopScrib(line, w).Scribbler!;
                double r = 0, g = 0, b = 0;
                if (x >= 0 && y >= 0 && x < h.Width && y < h.Height)
                {
                    int i = (y * h.Width + x) * 4;
                    r = h.Pixels[i]; g = h.Pixels[i + 1]; b = h.Pixels[i + 2];
                }
                Push(Value.OfArr(new[] { Value.OfNum(r), Value.OfNum(g), Value.OfNum(b) }));
                return true;
            }
            case "SCRIBBLERWIDTH":              // ( scribbler -- n )
                PushNum(PopScrib(line, w).Scribbler!.Width);
                return true;
            case "SCRIBBLERHEIGHT":             // ( scribbler -- n )
                PushNum(PopScrib(line, w).Scribbler!.Height);
                return true;
            case "SCRIBBLERBLIT":               // ( scribbler -- scribbler )
            {
                Value v = PopScrib(line, w);
                ScribblerHandle h = v.Scribbler!;
                h.OnBlit?.Invoke(h.Pixels, h.Width, h.Height);   // headless: no-op
                Push(v);
                return true;
            }
            case "SCRIBBLERCLOSE":              // ( scribbler -- scribbler ) idempotent
            {
                Value v = PopScrib(line, w);
                ScribblerHandle h = v.Scribbler!;
                if (h.MarkClosed())             // exactly once, against window teardown too
                {
                    ScribblerRegistry.NoteClosed();
                    h.OnClose?.Invoke();        // mill: destroy the window (bookkeeping done here)
                    h.Signal.Release();         // teardown release — wake any waiter
                }
                Push(v);
                return true;
            }
            case "SCRIBBLERTITLE":              // ( scribbler title -- scribbler )
            {
                string title = PopStr(line, w);
                Value v = PopScrib(line, w);
                v.Scribbler!.Title = title;
                v.Scribbler.OnSetTitle?.Invoke(title);
                Push(v);
                return true;
            }
            case "SCRIBBLERSAVE":               // ( scribbler path -- scribbler )
            {
                // The picture as it stands, straight from the pixel buffer —
                // no window is consulted, so this works headless and under
                // --no-window, which is the point of it. Nothing blits first:
                // saving and showing are separate requests, and a program
                // that wants both says both.
                string path = PopStr(line, w);
                Value v = PopScrib(line, w);
                ScribblerHandle h = v.Scribbler!;
                try
                {
                    Png.Write(path, h.Pixels, h.Width, h.Height);
                }
                catch (Exception e) when (e is not ShoddyError)
                {
                    throw Die(line, $"ScribblerSave: cannot write '{path}'");
                }
                Push(v);
                return true;
            }
            case "SCRIBBLERPLACE":              // ( scribbler x y -- scribbler )
            {
                // Desktop pixels of the window's top-left corner. Recorded on
                // the handle as well as posted, so a headless handle answers
                // for where it was asked to go — and a window that has not
                // been created yet is not a special case for the caller.
                // A window manager is free to ignore it; nothing here checks.
                int py = (int)PopNum(line, w), px = (int)PopNum(line, w);
                Value v = PopScrib(line, w);
                v.Scribbler!.PlaceX = px;
                v.Scribbler.PlaceY = py;
                v.Scribbler.OnPlace?.Invoke(px, py);
                Push(v);
                return true;
            }
            case "SCRIBBLERPOLL":               // ( scribbler -- arr ) kind 0 = queue empty
            {
                ScribblerHandle h = PopScrib(line, w).Scribbler!;
                Push(EventArray(h.TryTake(out ScribblerEvent ev) ? ev : null));
                return true;
            }
            case "SCRIBBLERWAIT":               // ( scribbler -- arr ) zero CPU while waiting
            {
                ScribblerHandle h = PopScrib(line, w).Scribbler!;
                while (true)
                {
                    if (h.TryTake(out ScribblerEvent ev)) { Push(EventArray(ev)); return true; }
                    if (h.Closed)               // teardown wake with an empty queue
                    {
                        Push(EventArray(new ScribblerEvent
                        {
                            Type = ScribblerEvent.Kind.Quit, At = Ticker.Now,
                        }));
                        return true;
                    }
                    if (h.OnBlit == null)       // headless: nothing will ever wake it
                        throw Die(line, "ScribblerWait: no window backs this scribbler — the wait would never wake");
                    h.Signal.Wait();
                }
            }
            case "SCRIBBLERSETINTERVAL":        // ( scribbler ms -- scribbler ) 0 = off
            {
                int ms = (int)PopNum(line, w);
                Value v = PopScrib(line, w);
                v.Scribbler!.OnSetInterval?.Invoke(Math.Max(ms, 0));   // headless: no-op
                Push(v);
                return true;
            }

            /* ---- the buzzer (no window required) ----
               All seven words are zero-result and fire-and-forget: the
               runtime validates — bad arguments are program bugs and
               raise even in silence — then calls through BuzzerRegistry,
               where null means headless and the word does nothing.
               Nothing ever blocks or waits on sound. */
            case "SOUND":                       // ( freq ms -- ) anonymous pool
            {
                double ms = PopNum(line, w), freq = PopNum(line, w);
                if (freq <= 0)
                    throw Die(line, $"Sound: frequency must be positive, got {Format.Num(freq)}");
                if (ms < 0)
                    throw Die(line, $"Sound: duration must be >= 0 ms, got {Format.Num(ms)}");
                if (ms > 0) BuzzerRegistry.Sound?.Invoke(freq, ms);    // 0 ms: legal no-op
                return true;
            }
            case "NOTEON":                      // ( ch freq -- ) hold until NOTEOFF
            {
                double freq = PopNum(line, w);
                int ch = PopBuzzerChannel(line, w, "NoteOn");
                if (freq <= 0)
                    throw Die(line, $"NoteOn: frequency must be positive, got {Format.Num(freq)}");
                buzzQueueEnd[ch] = 0;           // a held note flushes the channel's queue
                BuzzerRegistry.NoteOn?.Invoke(ch, freq);
                return true;
            }
            case "NOTEOFF":                     // ( ch -- ) nothing held: no-op
            {
                // Pop before the ?.Invoke — a null-conditional never
                // evaluates its arguments, and validation must run in
                // silence too.
                int ch = PopBuzzerChannel(line, w, "NoteOff");
                BuzzerRegistry.NoteOff?.Invoke(ch);
                return true;
            }
            case "SOUNDQUEUE":                  // ( ch freq ms -- ) back-to-back; freq 0 = rest
            {
                double ms = PopNum(line, w), freq = PopNum(line, w);
                int ch = PopBuzzerChannel(line, w, "SoundQueue");
                if (freq < 0)
                    throw Die(line, $"SoundQueue: frequency must be positive (or 0 for a rest), got {Format.Num(freq)}");
                if (ms < 0)
                    throw Die(line, $"SoundQueue: duration must be >= 0 ms, got {Format.Num(ms)}");
                // The cap is queued-ahead TIME, tracked here so it raises
                // headless too: the seam has no drain feedback, so pending
                // notes cannot be counted — but the drain instant is exact,
                // because playback is back-to-back from the moment of
                // queueing. Blocking would violate "sound never blocks";
                // dropping notes would corrupt the music; loud is right.
                double now = Ticker.Now;
                double end = Math.Max(buzzQueueEnd[ch], now) + ms;
                if (end - now > BuzzerQueueCapMs)
                    throw Die(line, $"SoundQueue: more than {BuzzerQueueCapMs / 60_000} minutes queued ahead on channel {ch} — feed long scores incrementally from Tick events");
                buzzQueueEnd[ch] = end;
                BuzzerRegistry.Queue?.Invoke(ch, freq, ms);
                return true;
            }
            case "SOUNDSTOP":                   // ( ch -- ) release held note, flush queue
            {
                int ch = PopBuzzerChannel(line, w, "SoundStop");
                buzzQueueEnd[ch] = 0;
                BuzzerRegistry.Stop?.Invoke(ch);
                return true;
            }
            case "SOUNDGAIN":                   // ( ch vol -- ) 0..1, sticky per channel
            {
                double vol = PopNum(line, w);
                int ch = PopBuzzerChannel(line, w, "SoundGain");
                if (vol < 0 || vol > 1)
                    throw Die(line, $"SoundGain: volume must be 0..1, got {Format.Num(vol)}");
                BuzzerRegistry.Gain?.Invoke(ch, vol);
                return true;
            }
            case "SOUNDWAVE":                   // ( ch wave -- ) 0 square, 1 triangle, 2 sine; sticky
            {
                double wv = PopNum(line, w);
                int ch = PopBuzzerChannel(line, w, "SoundWave");
                if (wv != 0 && wv != 1 && wv != 2)
                    throw Die(line, $"SoundWave: wave must be 0 (square), 1 (triangle) or 2 (sine), got {Format.Num(wv)}");
                BuzzerRegistry.Wave?.Invoke(ch, (int)wv);
                return true;
            }
        }
        return false;
    }

    // ---- buzzer bookkeeping ---------------------------------------------

    // Per-channel Ticker instant the queue drains, indexed 1..8. The
    // SOUNDQUEUE cap; NOTEON and SOUNDSTOP reset it (both flush the queue).
    const double BuzzerQueueCapMs = 5 * 60_000;
    readonly double[] buzzQueueEnd = new double[9];

    int PopBuzzerChannel(int line, string w, string who)
    {
        double c = PopNum(line, w);
        if (c < 1 || c > 8 || c != Math.Floor(c))
            throw Die(line, $"{who}: channel must be 1..8, got {Format.Num(c)}");
        return (int)c;
    }

    Value PopScrib(int line, string who)
    {
        Value v = Pop(line);
        if (v.T != VType.Scribbler)
            throw Die(line, $"{who} expects a SCRIBBLER, got {Value.TypeName(v.T)}");
        return v;
    }

    static byte Chan(double d) => (byte)Math.Clamp((int)d, 0, 255);

    /// <summary>The 8-element event array SCRIBBLERPOLL and SCRIBBLERWAIT
    /// return: kind, x, y, button, key, keyChar, mods, at. Null means the
    /// queue was empty — kind 0 (None) with every field zeroed. A flat
    /// Array, not a record: a builtin cannot reach a TypeDef, so decoding
    /// into the sum type is machines/scribbler.shoddy's job.</summary>
    static Value EventArray(ScribblerEvent? e) => Value.OfArr(new[]
    {
        Value.OfNum(e == null ? 0 : (int)e.Type),
        Value.OfNum(e?.X ?? 0),
        Value.OfNum(e?.Y ?? 0),
        Value.OfNum(e?.Button ?? 0),
        Value.OfNum(e?.Key ?? 0),
        Value.OfStr(e?.KeyChar ?? ""),
        Value.OfNum(e == null ? 0 : (int)e.Mods),
        Value.OfNum(e?.At ?? 0),
    });

    /// <summary>The full builtin vocabulary — used by the compiler to
    /// resolve words at weave time. Must match the switch above.</summary>
    public static readonly HashSet<string> BuiltinWords = new()
    {
        "DUP", "DROP", "SWAP", "OVER", "ROT", "NIP", "TUCK", "DEPTH",
        "+", "-", "*", "/", "MOD", "WRAP", "NEGATE", "ABS", "SGN", "MIN", "MAX", "SQR",
        "FLOOR", "CEIL", "ROUND", "FIX", "^", "SIN", "COS", "TAN", "ATN", "ATN2",
        "ASIN", "ACOS", "TANH", "EXP", "LOG", "LOG10", "PI", "RND", "SEED",
        "ERF", "GAMMAP", "BETAI",
        "ERROR", "ASSERT", "INSTR",
        "=", "<>", "<", ">", "<=", ">=",
        "AND", "OR", "NOT", "TRUE", "FALSE",
        "&", "LEN", "STR", "VAL", "ISNUMERIC", "VALOR", "LEFT", "RIGHT", "MID", "CHR", "ASC",
        "UPPER", "LOWER",
        "PRINT", "READFILE", "TRYREADFILE", "WRITEFILE", "APPENDFILE",
        "TRYWRITEFILE", "FILEEXISTS",
        "DELETEFILE", "BOPEN", "BCLOSE", "SEEK", "BPOS", "BSIZE",
        "PUTNUM", "GETNUM", "PUTBOOL", "GETBOOL", "PUTSTR", "GETSTR",
        "TCPCONNECT", "TCPLISTEN", "TCPACCEPT", "TCPSEND", "TCPRECV",
        "TCPEOF", "TCPPOLL", "TCPPEER", "TCPCLOSE", "TCPSECURE",
        "INPUT", "INPUTLINE", "INKEY", "ARGS",
        "CALL", "IFTE", "MAP", "FILTER", "FOLD", "EACH", "TIMES", "RANGE",
        "LENGTH", "REVERSE", "CONCAT", "SORT",
        "ISEMPTY", "FIRST", "NTH", "SETNTH", "DIM", "TOARRAY", "TOLIST",
        "REST", "PREPEND",
        "TICKS", "SLEEP", "CLOCK",
        "SCRIBBLEROPEN", "SCRIBBLERPIXEL", "SCRIBBLERFILL", "SCRIBBLERTEXT",
        "SCRIBBLERGETPIXEL", "SCRIBBLERWIDTH", "SCRIBBLERHEIGHT",
        "SCRIBBLERBLIT", "SCRIBBLERCLOSE", "SCRIBBLERTITLE", "SCRIBBLERPOLL",
        "SCRIBBLERWAIT", "SCRIBBLERSETINTERVAL", "SCRIBBLERSAVE", "SCRIBBLERPLACE",
        "SOUND", "NOTEON", "NOTEOFF", "SOUNDQUEUE", "SOUNDSTOP", "SOUNDGAIN",
        "SOUNDWAVE",
    };

    // ---- special functions ----------------------------------------------
    // erf, the regularized lower incomplete gamma P(a,x), and the
    // regularized incomplete beta I_x(a,b) are not in the BCL. These are
    // the classic implementations — Lanczos log-gamma, power series and
    // modified-Lentz continued fractions — accurate to ~1e-14 relative
    // over the ranges the stats machine uses.

    /// <summary>ln Γ(x) for x &gt; 0 (Lanczos, g=5, n=6).</summary>
    static double GammLn(double x)
    {
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        ser += 76.18009172947146 / ++y;
        ser += -86.50532032941677 / ++y;
        ser += 24.01409824083091 / ++y;
        ser += -1.231739572450155 / ++y;
        ser += 0.1208650973866179e-2 / ++y;
        ser += -0.5395239384953e-5 / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }

    /// <summary>Regularized lower incomplete gamma P(a, x), a &gt; 0, x ≥ 0.
    /// Series for x &lt; a+1, continued fraction for the complement above.</summary>
    static double GammaP(double a, double x)
    {
        if (x == 0) return 0;
        if (x < a + 1)                          // series converges fast here
        {
            double ap = a, sum = 1 / a, del = sum;
            for (int k = 0; k < 500; k++)
            {
                ap += 1;
                del *= x / ap;
                sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * 1e-16) break;
            }
            return sum * Math.Exp(-x + a * Math.Log(x) - GammLn(a));
        }
        else                                    // Lentz continued fraction for Q(a,x)
        {
            double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
            for (int k = 1; k <= 500; k++)
            {
                double an = -k * (k - a);
                b += 2;
                d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
                c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
                d = 1 / d;
                double del = d * c;
                h *= del;
                if (Math.Abs(del - 1) < 1e-16) break;
            }
            return 1 - Math.Exp(-x + a * Math.Log(x) - GammLn(a)) * h;
        }
    }

    /// <summary>Continued fraction for the incomplete beta (modified Lentz);
    /// only valid on the fast-converging side, which BetaI arranges.</summary>
    static double BetaCf(double a, double b, double x)
    {
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < 1e-300) d = 1e-300;
        d = 1 / d;
        double h = d;
        for (int m = 1; m <= 500; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = 1 + aa / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = 1 + aa / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-16) break;
        }
        return h;
    }

    /// <summary>Regularized incomplete beta I_x(a, b), a,b &gt; 0, x in [0,1].</summary>
    static double BetaI(double a, double b, double x)
    {
        if (x == 0) return 0;
        if (x == 1) return 1;
        double bt = Math.Exp(GammLn(a + b) - GammLn(a) - GammLn(b)
                             + a * Math.Log(x) + b * Math.Log(1 - x));
        if (x < (a + 1) / (a + b + 2))
            return bt * BetaCf(a, b, x) / a;
        return 1 - bt * BetaCf(b, a, 1 - x) / b;    // symmetry: faster side
    }

    /// <summary>C strtod semantics: parse the longest numeric prefix
    /// (sign, digits, decimal point, exponent); false if none.</summary>
    public static bool Strtod(string s, out double v)
    {
        v = 0;
        int i0 = 0;
        while (i0 < s.Length && char.IsWhiteSpace(s[i0])) i0++;
        int j = i0;
        if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
        int digits = 0;
        while (j < s.Length && char.IsAsciiDigit(s[j])) { j++; digits++; }
        if (j < s.Length && s[j] == '.')
        {
            j++;
            while (j < s.Length && char.IsAsciiDigit(s[j])) { j++; digits++; }
        }
        if (digits == 0) return false;
        int end = j;
        if (j < s.Length && (s[j] == 'e' || s[j] == 'E'))
        {
            int m = j + 1;
            if (m < s.Length && (s[m] == '+' || s[m] == '-')) m++;
            int ed = 0;
            while (m < s.Length && char.IsAsciiDigit(s[m])) { m++; ed++; }
            if (ed > 0) end = m;
        }
        return double.TryParse(s[i0..end], NumberStyles.Float, Inv, out v);
    }
}
