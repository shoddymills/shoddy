# Join each blank-line-delimited block into a single line.
#
# The game's message text is stored pre-wrapped, and Mungo Caverns renamed
# the dwarves to curmudgeons -- five characters longer.  Every paragraph
# mentioning one therefore breaks at different columns than the C's
# recorded output does, even when the words are identical.  Collapsing
# each paragraph to a single line compares content and paragraph
# structure while ignoring where the wrap fell.
#
# The tradeoff is deliberate and worth stating: this harness cannot see
# a regression that changes only line breaks within a paragraph.  It can
# see every change to words, order, spacing between tokens, blank-line
# structure, and prompt placement.

{ sub(/\r$/, "") }

/^$/ {
    if (block != "") { print block; block = "" }
    print ""
    next
}

{ if (block == "") block = $0; else block = block " " $0 }

END { if (block != "") print block }
