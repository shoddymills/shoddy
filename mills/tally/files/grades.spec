# tally spec - study hours against final marks
#
# Run from this directory:
#     ./build.sh run files/grades.spec

data.file      = mills/tally/files/grades.csv
data.trim      = yes

derive.Z       = zscores FINAL
derive.RES     = residuals HOURS FINAL

report.count   = count
report.stats   = describe HOURS MIDTERM FINAL
report.corr    = correl HOURS FINAL
report.line    = fit HOURS FINAL
report.groups  = freq GROUP
report.strays  = outliers FINAL

chart.type     = scatter
chart.x        = HOURS
chart.y        = FINAL
chart.fit      = yes
chart.title    = HOURS AGAINST FINAL MARK

window.size    = 480 320
window.at      = 120 90
window.capture = mills/tally/files/grades.png
window.show    = no
