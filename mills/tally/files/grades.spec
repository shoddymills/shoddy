# tally spec - study hours against final marks
#
#     ./build.sh run             show the chart in a window
#     ./build.sh capture         write the PNG, nothing on screen
#
# Paths below are relative to the directory tally is RUN from, and both
# build wrappers run from the repo root - hence mills/tally/... rather
# than files/... A spec is a small thing you keep beside your data, and
# a path that meant different things depending on which folder the spec
# happened to live in would be a path nobody could read.
#
# A comment is a line whose first non-space character is #. There are no
# trailing comments: data.comment takes # as its VALUE, and a parser
# that stripped from the first # onwards would eat it.

# ---- 1. read it ----------------------------------------------------
# The first record is the header, so every line after this talks about
# columns by name. data.sep, data.comment and data.header are there for
# files that are not plain comma-separated with a header row.

data.file      = mills/tally/files/grades.csv
data.trim      = yes

# ---- 2. derive: add columns ----------------------------------------
# Each line appends ONE new column to every row. The key after the dot
# names it; the value is a verb and the columns it works on - never an
# expression. Derives run in file order, so a later one may name an
# earlier one's column.
#
# Z   restates each mark in standard deviations from the class mean
#     (mean 83.7, sample SD 16.262). That is what makes it comparable
#     across classes, where a raw mark is not.
# RES fits FINAL = 2.778 x HOURS + 48.147 - the same line report.line
#     prints - and subtracts, per row, what that line PREDICTED from
#     what actually happened. The column of what the straight line
#     failed to explain. JEAN's -8.48 is the largest miss and she scored
#     98: 21 hours predicts 106.5, and marks stop at 100.
#
# Both see all ten rows, because derive runs BEFORE filter. Adding a
# filter below would not recompute Z among the survivors.

derive.Z       = zscores FINAL
derive.RES     = residuals HOURS FINAL

# ---- 3. filter: drop rows ------------------------------------------
# None here, so all ten go through. Several apply in file order, each
# narrowing what the last one left:
#
#     filter.keen = above HOURS 10
#     filter.sane = inliers FINAL        (the 1.5 x IQR fences)

# ---- 4. report: print it -------------------------------------------
# One block of output per line, in file order, each a verb over a word
# stats.shoddy already exports. This is what files/expected.out holds,
# and what the headless suite grades line by line.

report.count   = count
report.stats   = describe HOURS MIDTERM FINAL
report.corr    = correl HOURS FINAL
report.line    = fit HOURS FINAL
report.groups  = freq GROUP
report.strays  = outliers FINAL

# ---- 5. draw it ----------------------------------------------------
# chart.fit lays the regression line over the dots, so the residuals
# derived above are the vertical gaps you can see in the picture.
# window.show = no says the PNG is the whole output: draw, save, exit,
# and do not wait for anyone to dismiss a window.

chart.type     = scatter
chart.x        = HOURS
chart.y        = FINAL
chart.fit      = yes
chart.title    = HOURS AGAINST FINAL MARK

window.size    = 480 320
window.at      = 120 90
window.capture = mills/tally/files/grades.png
window.show    = no
