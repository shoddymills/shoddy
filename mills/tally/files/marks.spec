# tally spec - the shape of the final marks, and who sat where
#
#     ./build.sh capture files/marks.spec
#
# Paths are relative to the directory the mill is run from; both build
# wrappers run from this mill's folder, so files/... names what sits
# beside this spec.

data.file      = files/grades.csv

# Two derived columns, each one verb and its arguments - never an
# expression. TOTAL is the two papers added; SHARE is that as a
# percentage of the class total.
derive.TOTAL   = add MIDTERM FINAL
derive.SHARE   = pct TOTAL

report.count   = count
report.stats   = describe MIDTERM FINAL TOTAL
report.spread  = outliers TOTAL
report.groups  = freq GROUP

chart.type     = histogram
chart.x        = FINAL
chart.bins     = 6
chart.title    = DISTRIBUTION OF FINAL MARKS

window.size    = 480 320
window.capture = files/marks.png
window.show    = no
