# tally spec - each student's share of the class total, as a bar chart
#
#     ./build.sh capture mills/tally/files/share.spec

data.file      = files/grades.csv

derive.TOTAL   = add MIDTERM FINAL

# Only the students who did more than ten hours, so the chart has room
# for its labels. filter.* runs after derive.* and before report.*.
filter.keen    = above HOURS 10

report.count   = count
report.line    = fit HOURS TOTAL

chart.type     = bar
chart.y        = TOTAL
chart.labels   = STUDENT
chart.title    = TOTAL MARKS, TEN HOURS OR MORE

window.size    = 480 320
window.capture = files/share.png
window.show    = no
