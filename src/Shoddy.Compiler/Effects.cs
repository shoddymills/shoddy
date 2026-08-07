// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Compiler;

using Shoddy.Runtime;

/// <summary>The stack effect of every builtin, as (pops, pushes) — the
/// linter's ground truth, transcribed from Engine's own `( a b -- c )`
/// comments. A test asserts this table covers <see cref="Engine.BuiltinWords"/>
/// exactly, so a new builtin cannot ship without declaring its effect here
/// (or declaring it dynamic).
///
/// CALL is the one genuinely dynamic builtin: it pops a quotation and runs
/// it, so its net effect is the quotation's. The checker treats any Def
/// that reaches a dynamic builtin as unknown rather than guessing.</summary>
public static class Effects
{
    public readonly record struct Effect(int Pops, int Pushes);

    /// <summary>Builtins whose effect depends on a runtime value.</summary>
    public static readonly HashSet<string> Dynamic = new() { "CALL" };

    public static readonly Dictionary<string, Effect> Builtin = new()
    {
        // stack shuffles
        ["DUP"] = new(1, 2), ["DROP"] = new(1, 0), ["SWAP"] = new(2, 2),
        ["OVER"] = new(2, 3), ["ROT"] = new(3, 3), ["NIP"] = new(2, 1),
        ["TUCK"] = new(2, 3), ["DEPTH"] = new(0, 1),
        // arithmetic
        ["+"] = new(2, 1), ["-"] = new(2, 1), ["*"] = new(2, 1),
        ["/"] = new(2, 1), ["MOD"] = new(2, 1), ["WRAP"] = new(2, 1),
        ["NEGATE"] = new(1, 1), ["ABS"] = new(1, 1), ["SGN"] = new(1, 1),
        ["MIN"] = new(2, 1), ["MAX"] = new(2, 1), ["SQR"] = new(1, 1),
        ["FLOOR"] = new(1, 1), ["CEIL"] = new(1, 1), ["ROUND"] = new(1, 1),
        ["FIX"] = new(1, 1), ["^"] = new(2, 1),
        ["SIN"] = new(1, 1), ["COS"] = new(1, 1), ["TAN"] = new(1, 1),
        ["ATN"] = new(1, 1), ["ATN2"] = new(2, 1), ["ASIN"] = new(1, 1),
        ["ACOS"] = new(1, 1), ["TANH"] = new(1, 1), ["EXP"] = new(1, 1),
        ["LOG"] = new(1, 1), ["LOG10"] = new(1, 1), ["PI"] = new(0, 1),
        ["RND"] = new(0, 1), ["SEED"] = new(1, 0),
        ["ERF"] = new(1, 1), ["GAMMAP"] = new(2, 1), ["BETAI"] = new(3, 1),
        // errors and testing
        ["ERROR"] = new(1, 0), ["ASSERT"] = new(2, 0), ["INSTR"] = new(2, 1),
        // comparison and logic
        ["="] = new(2, 1), ["<>"] = new(2, 1), ["<"] = new(2, 1),
        [">"] = new(2, 1), ["<="] = new(2, 1), [">="] = new(2, 1),
        ["AND"] = new(2, 1), ["OR"] = new(2, 1), ["NOT"] = new(1, 1),
        ["TRUE"] = new(0, 1), ["FALSE"] = new(0, 1),
        // strings
        ["&"] = new(2, 1), ["LEN"] = new(1, 1), ["STR"] = new(1, 1),
        ["VAL"] = new(1, 1), ["ISNUMERIC"] = new(1, 1), ["VALOR"] = new(2, 1),
        ["LEFT"] = new(2, 1), ["RIGHT"] = new(2, 1), ["MID"] = new(3, 1),
        ["CHR"] = new(1, 1), ["ASC"] = new(1, 1),
        ["UPPER"] = new(1, 1), ["LOWER"] = new(1, 1),
        // console and files
        ["PRINT"] = new(1, 0), ["READFILE"] = new(1, 1),
        ["TRYREADFILE"] = new(1, 1),
        ["WRITEFILE"] = new(2, 0), ["APPENDFILE"] = new(2, 0),
        ["TRYWRITEFILE"] = new(2, 1), ["FILEEXISTS"] = new(1, 1),
        ["DELETEFILE"] = new(1, 0), ["TRYDELETEFILE"] = new(1, 1),
        ["BOPEN"] = new(1, 1), ["TRYBOPEN"] = new(1, 1),
        ["BCLOSE"] = new(1, 0), ["SEEK"] = new(2, 0),
        ["BPOS"] = new(1, 1), ["BSIZE"] = new(1, 1),
        ["PUTNUM"] = new(2, 0), ["GETNUM"] = new(1, 1),
        ["PUTBOOL"] = new(2, 0), ["GETBOOL"] = new(1, 1),
        ["PUTSTR"] = new(3, 0), ["GETSTR"] = new(2, 1),
        // network (gated)
        ["TCPCONNECT"] = new(2, 1), ["TRYTCPCONNECT"] = new(2, 1),
        ["TRYTCPREQUEST"] = new(4, 1),
        ["NETALLOWED"] = new(0, 1),
        ["TCPLISTEN"] = new(2, 1),
        ["TCPACCEPT"] = new(1, 1), ["TCPSEND"] = new(2, 0),
        ["TCPRECV"] = new(2, 1), ["TCPEOF"] = new(1, 1),
        ["TCPPOLL"] = new(1, 1), ["TCPPEER"] = new(1, 1),
        ["TCPCLOSE"] = new(1, 0), ["TCPSECURE"] = new(2, 0),
        // input
        ["INPUT"] = new(1, 1), ["INPUTLINE"] = new(1, 1),
        ["INKEY"] = new(0, 1), ["ARGS"] = new(0, 1),
        // higher-order (fixed shells; CALL alone is dynamic)
        ["CALL"] = new(1, 0),           // shell only — see Dynamic
        ["IFTE"] = new(3, 1), ["MAP"] = new(2, 1), ["FILTER"] = new(2, 1),
        ["FOLD"] = new(3, 1), ["EACH"] = new(2, 0), ["TIMES"] = new(2, 0),
        ["RANGE"] = new(2, 1),
        // sequences
        ["LENGTH"] = new(1, 1), ["REVERSE"] = new(1, 1),
        ["CONCAT"] = new(2, 1), ["SORT"] = new(1, 1),
        ["ISEMPTY"] = new(1, 1), ["FIRST"] = new(1, 1), ["NTH"] = new(2, 1),
        ["SETNTH"] = new(3, 1), ["DIM"] = new(2, 1),
        ["TOARRAY"] = new(1, 1), ["TOLIST"] = new(1, 1),
        ["REST"] = new(1, 1), ["PREPEND"] = new(2, 1),
        // time
        ["TICKS"] = new(0, 1), ["SLEEP"] = new(1, 0), ["CLOCK"] = new(0, 1),
        // scribbler
        ["SCRIBBLEROPEN"] = new(2, 1), ["TRYSCRIBBLEROPEN"] = new(2, 1),
        ["SCRIBBLEROF"] = new(1, 1), ["SCRIBBLERSHUT"] = new(1, 1), ["SCRIBBLERPIXEL"] = new(6, 1),
        ["SCRIBBLERFILL"] = new(4, 1), ["SCRIBBLERTEXT"] = new(8, 1),
        ["SCRIBBLERGETPIXEL"] = new(3, 1), ["SCRIBBLERWIDTH"] = new(1, 1),
        ["SCRIBBLERHEIGHT"] = new(1, 1), ["SCRIBBLERBLIT"] = new(1, 1),
        ["SCRIBBLERCLOSE"] = new(1, 1), ["SCRIBBLERTITLE"] = new(2, 1),
        ["SCRIBBLERPOLL"] = new(1, 1), ["SCRIBBLERWAIT"] = new(1, 1),
        ["SCRIBBLERSETINTERVAL"] = new(2, 1),
        ["SCRIBBLERSAVE"] = new(2, 1), ["SCRIBBLERPLACE"] = new(3, 1),
        // sound
        ["SOUND"] = new(2, 0), ["NOTEON"] = new(2, 0), ["NOTEOFF"] = new(1, 0),
        ["SOUNDQUEUE"] = new(3, 0), ["SOUNDSTOP"] = new(1, 0),
        ["SOUNDGAIN"] = new(2, 0), ["SOUNDWAVE"] = new(2, 0),
    };
}
