#include "MainWindow.h"

#include <QApplication>
#include <QCoreApplication>

int main(int argc, char *argv[])
{
    QApplication application(argc, argv);
    QCoreApplication::setApplicationName("iPad Tablet Backend");
    QCoreApplication::setApplicationVersion("0.0.1");
    QCoreApplication::setOrganizationName("Davidio1777");
    QCoreApplication::setOrganizationDomain("dev.david");

    MainWindow window;
    window.show();
    return application.exec();
}
